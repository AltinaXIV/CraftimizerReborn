using Craftimizer.Simulator.Actions;

namespace Craftimizer.Simulator;

public readonly record struct SimulationState
{
    public SimulationInput Input { get; init; }

    public int ActionCount => ActionHistory.Count;

    public int StepCount { get; init; }
    public int Progress { get; init; }
    public int Quality { get; init; }
    public int Durability { get; init; }
    public int CP { get; init; }
    public Condition Condition { get; init; }
    public Effects ActiveEffects { get; init; }
    public List<ActionType> ActionHistory { get; init; }

    // https://github.com/ffxiv-teamcraft/simulator/blob/0682dfa76043ff4ccb38832c184d046ceaff0733/src/model/tables.ts#L2
    private static readonly int[] HQPercentTable = {
        1, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 6, 6, 6, 6, 7, 7, 7, 7, 8, 8, 8,
        9, 9, 9, 10, 10, 10, 11, 11, 11, 12, 12, 12, 13, 13, 13, 14, 14, 14, 15, 15, 15, 16, 16, 17, 17,
        17, 18, 18, 18, 19, 19, 20, 20, 21, 22, 23, 24, 26, 28, 31, 34, 38, 42, 47, 52, 58, 64, 68, 71,
        74, 76, 78, 80, 81, 82, 83, 84, 85, 86, 87, 88, 89, 90, 91, 92, 94, 96, 98, 100
    };
    public int HQPercent => HQPercentTable[(int)Math.Clamp((float)Quality / Input.Recipe.MaxQuality * 100, 0, 100)];

    public bool IsFirstStep => StepCount == 0;

    public SimulationState(SimulationInput input)
    {
        Input = input;

        StepCount = 0;
        Progress = 0;
        Quality = 0;
        Durability = Input.Recipe.MaxDurability;
        CP = Input.Stats.CP;
        Condition = Condition.Normal;
        ActiveEffects = new();
        ActionHistory = new();
    }
}
