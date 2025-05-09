namespace Craftimizer.Simulator.Actions;

internal sealed class PreciseTouch() : BaseAction(
    ActionCategory.Quality, 53, 100128,
    increasesQuality: true,
    defaultCPCost: 18,
    defaultEfficiency: 150
    )
{
    public override bool CouldUse(RotationSimulator s) =>
        (s.Condition is Condition.Good or Condition.Excellent || s.HasEffect(EffectType.HeartAndSoul))
        && base.CouldUse(s);

    public override void UseSuccess(RotationSimulator s)
    {
        base.UseSuccess(s);
        s.StrengthenEffect(EffectType.InnerQuiet);
        if (s.Condition is not (Condition.Good or Condition.Excellent))
            s.RemoveEffect(EffectType.HeartAndSoul);
    }
}
