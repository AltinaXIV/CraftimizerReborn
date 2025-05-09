using Craftimizer.Simulator.Actions;
using Craftimizer.Simulator;
using System.Diagnostics.Contracts;
using System.Runtime.CompilerServices;
using Node = Craftimizer.Solver.ArenaNode<Craftimizer.Solver.SimulationNode>;
using System.Runtime.Intrinsics;

namespace Craftimizer.Solver;

// https://github.com/alostsock/crafty/blob/cffbd0cad8bab3cef9f52a3e3d5da4f5e3781842/crafty/src/simulator.rs
public sealed class MCTS
{
    private readonly MCTSConfig config;
    private readonly Node rootNode;
    private readonly RootScores rootScores;

    public const int ProgressUpdateFrequency = 1 << 10;
    private const int StaleProgressThreshold = 1 << 12;

    public float MaxScore => rootScores.MaxScore;

    public MCTS(in MCTSConfig config, in SimulationState state)
    {
        this.config = config;
        var sim = new RotationSimulator(config.ActionPool, config.MaxStepCount, state);
        rootNode = new(new(
            state,
            null,
            sim.CompletionState,
            sim.AvailableActionsHeuristic(config.StrictActions)
        ));
        rootScores = new();
    }

    private static SimulationNode Execute(RotationSimulator rotationSimulator, in SimulationState state, ActionType action, bool strict)
    {
        var newState = rotationSimulator.ExecuteUnchecked(state, action);
        return new(
            newState,
            action,
            rotationSimulator.CompletionState,
            rotationSimulator.AvailableActionsHeuristic(strict)
        );
    }

    private static Node ExecuteActions(RotationSimulator rotationSimulator, Node startNode, ReadOnlySpan<ActionType> actions, bool strict)
    {
        foreach (var action in actions)
        {
            var state = startNode.State;
            if (state.IsComplete)
                return startNode;

            if (!state.AvailableActions.HasAction(action))
                return startNode;
            state.AvailableActions.RemoveAction(action);

            startNode = startNode.Add(Execute(rotationSimulator, state.State, action, strict));
        }

        return startNode;
    }

    [Pure]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static (int arrayIdx, int subIdx) ChildMaxScore(in NodeScoresBuffer scores)
    {
        var length = scores.Count;
        var vecLength = Vector256<float>.Count;

        var max = (0, 0);
        var maxScore = 0f;
        for (var i = 0; length > 0; ++i)
        {
            var iterCount = Math.Min(vecLength, length);

            var m = scores.Data![i].MaxScore;

            var idx = Intrinsics.HMaxIndex(m, iterCount);

            if (m[idx] >= maxScore)
            {
                max = (i, idx);
                maxScore = m[idx];
            }

            length -= iterCount;
        }

        return max;
    }

    // Calculates the best child node to explore next
    // Exploitation: ((1 - w) * (s / v)) + (w * m)
    // Exploration: sqrt(c * ln(V) / v)
    // w = maxScoreWeightingConstant
    // s = score sum
    // m = max score
    // v = visits
    // V = parentVisits
    // c = explorationConstant

    // Somewhat based off of https://en.wikipedia.org/wiki/Monte_Carlo_tree_search#Exploration_and_exploitation
    // Here, w_i = (1-w)*score sum
    // n_i = visits
    // max score is tacked onto it
    // N_i = parent visits
    // c = exploration constant (but crafty places it inside the sqrt..?)
    [Pure]
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private static (int arrayIdx, int subIdx) EvalBestChild(
        float explorationConstant,
        float maxScoreWeightingConstant,
        int parentVisits,
        in NodeScoresBuffer scores)
    {
        var length = scores.Count;
        var vecLength = Vector256<float>.Count;

        var C = MathF.Sqrt(explorationConstant * MathF.Log(parentVisits));
        var w = maxScoreWeightingConstant;
        var W = 1f - w;
        var CVector = Vector256.Create(C);

        var max = (0, 0);
        var maxScore = 0f;
        for (var i = 0; length > 0; ++i)
        {
            var iterCount = Math.Min(vecLength, length);

            ref var chunk = ref scores.Data![i];
            var s = chunk.ScoreSum;
            var vInt = chunk.Visits;
            var m = chunk.MaxScore;

            vInt = Vector256.Max(vInt, Vector256<int>.One);
            var v = Vector256.ConvertToSingle(vInt);

            var exploitation = W * (s / v) + w * m;
            var exploration = CVector * Intrinsics.ReciprocalSqrt(v);
            var evalScores = exploitation + exploration;

            var idx = Intrinsics.HMaxIndex(evalScores, iterCount);

            if (evalScores[idx] >= maxScore)
            {
                max = (i, idx);
                maxScore = evalScores[idx];
            }

            length -= iterCount;
        }

        return max;
    }

    [Pure]
    private Node Select()
    {
        var node = rootNode;
        var nodeVisits = rootScores.Visits;

        float explorationConstant = config.ExplorationConstant, maxScoreWeightingConstant = config.MaxScoreWeightingConstant;
        while (true)
        {
            var expandable = !node.State.AvailableActions.IsEmpty;
            var likelyTerminal = node.Children.Count == 0;
            if (expandable || likelyTerminal)
                return node;

            // select the node with the highest score
            var at = EvalBestChild(explorationConstant, maxScoreWeightingConstant, nodeVisits, in node.ChildScores);
            nodeVisits = node.ChildScores.GetVisits(at);
            node = node.ChildAt(at)!;
        }
    }

    [SkipLocalsInit]
    private (Node ExpandedNode, float Score) ExpandAndRollout(Random random, RotationSimulator rotationSimulator, Node initialNode, Span<ActionType> actionBuffer)
    {
        ref var initialState = ref initialNode.State;
        // expand once
        if (initialState.IsComplete)
            return (initialNode, initialState.CalculateScore(config) ?? 0);

        var poppedAction = initialState.AvailableActions.PopRandom(random);
        var expandedNode = initialNode.Add(Execute(rotationSimulator, initialState.State, poppedAction, true));

        // playout to a terminal state
        var currentState = expandedNode.State.State;
        var currentCompletionState = expandedNode.State.SimulationCompletionState;
        var currentActions = expandedNode.State.AvailableActions;

        if (currentState.ActionCount < config.MaxStepCount)
        {
            var actions = actionBuffer[..Math.Min(config.MaxStepCount - currentState.ActionCount, config.MaxRolloutStepCount)];
            byte actionCount = 0;
            while (SimulationNode.GetCompletionState(currentCompletionState, currentActions) == CompletionState.Incomplete &&
                actionCount < actions.Length)
            {
                var nextAction = currentActions.SelectRandom(random);
                actions[actionCount++] = nextAction;
                currentState = rotationSimulator.ExecuteUnchecked(currentState, nextAction);
                currentCompletionState = rotationSimulator.CompletionState;
                if (currentCompletionState != CompletionState.Incomplete)
                    break;
                currentActions = rotationSimulator.AvailableActionsHeuristic(true);
            }
        }

        var score = SimulationNode.CalculateScoreForState(currentState, currentCompletionState, config) ?? 0;
        return (expandedNode, score);
    }

    private void Backpropagate(Node startNode, float score)
    {
        while (true)
        {
            if (startNode == rootNode)
            {
                rootScores.Visit(score);
                break;
            }
            startNode.ParentScores!.Value.Visit(startNode.ChildIdx, score);

            startNode = startNode.Parent!;
        }
    }

    private bool AllNodesComplete()
    {
        static bool NodesIncomplete(Node node, Stack<Node> path)
        {
            path.Push(node);
            if (node.Children.Count == 0)
            {
                if (!node.State.AvailableActions.IsEmpty)
                    return true;
            }
            else
            {
                for (var i = 0; i < node.Children.Count; ++i)
                {
                    var n = node.ChildAt((i >> 3, i & 7))!;
                    if (NodesIncomplete(n, path))
                        return true;
                }
                path.Pop();
            }
            return false;
        }
        return !NodesIncomplete(rootNode, new());
    }

    [SkipLocalsInit]
    public unsafe void Search(int iterations, int maxIterations, ref int progress, CancellationToken token)
    {
        maxIterations = Math.Max(iterations, maxIterations);
        var simulator = new RotationSimulator(config.ActionPool, config.MaxStepCount, rootNode.State.State);
        var random = rootNode.State.State.Input.Random;
        var staleCounter = 0;
        var i = 0;

        Span<ActionType> actionBuffer = stackalloc ActionType[Math.Min(config.MaxStepCount, config.MaxRolloutStepCount)];
        for (; (i < iterations || MaxScore == 0); i++)
        {
            var selectedNode = Select();
            var (endNode, score) = ExpandAndRollout(random, simulator, selectedNode, actionBuffer);
            if (MaxScore == 0)
            {
                if (endNode == selectedNode)
                {
                    if (i >= maxIterations)
                        return;
                    if (staleCounter++ >= StaleProgressThreshold)
                    {
                        staleCounter = 0;
                        if (AllNodesComplete())
                            return;
                    }
                }
                else
                    staleCounter = 0;
            }

            Backpropagate(endNode, score);

            if ((i & (ProgressUpdateFrequency - 1)) == ProgressUpdateFrequency - 1)
            {
                token.ThrowIfCancellationRequested();
                Interlocked.Add(ref progress, ProgressUpdateFrequency);
            }
        }
        Interlocked.Add(ref progress, i & (ProgressUpdateFrequency - 1));
    }

    [Pure]
    public SolverSolution Solution()
    {
        var actions = new List<ActionType>();
        var node = rootNode;

        while (node.Children.Count != 0)
        {
            node = node.ChildAt(ChildMaxScore(in node.ChildScores))!;

            if (node.State.Action != null)
                actions.Add(node.State.Action.Value);
        }

        return new(actions, node.State.State);
    }
}
