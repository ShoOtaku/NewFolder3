using DeepDungeon.MechanicPlanning;

namespace MasterPlayer.MechanicRuntime;

// Requests keep their ordinary search result unless a complete mechanic
// proposal survives the same forecast and improves its completion margin.
internal struct MechanicRouteSelection(ControlPlan original)
{
    public ControlPlan Plan = original;
    public string? ProposalKind;
    public bool Replaced;
    public int Evaluations;
    private readonly double? _originalSlack = original.Assessment!.Value.CompletionSlack;
    public readonly MechanicRouteComparison? Comparison => ProposalKind == null ? null
        : new(ProposalKind, _originalSlack, Plan.Assessment!.Value.CompletionSlack, Evaluations);

    public void Evaluate(EncounterForwardProblem problem, ControlPlan candidate)
    {
        var score = problem.Evaluate(candidate.Velocities, candidate.Ends);
        ++Evaluations;
        if (score.GeometryFailures != 0 || score.Failures != 0 || score.CompletionSlack is not { } slack)
            return;
        var best = Plan.Assessment!.Value;
        if (best.GeometryFailures == 0 && best.Failures == 0 &&
            (score.ImpactMarginShortfall > best.ImpactMarginShortfall || slack <= (best.CompletionSlack ?? double.NegativeInfinity)))
            return;
        Replaced = true;
        Plan = candidate with { Assessment = score };
    }
}
