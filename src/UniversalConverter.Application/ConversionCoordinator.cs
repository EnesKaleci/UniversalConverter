using UniversalConverter.Contracts;
using UniversalConverter.Domain;

namespace UniversalConverter.Application;

public sealed class ConversionCoordinator(IConversionPlanner planner)
{
    public ConversionPlan Prepare(ConversionRequest request) => planner.Plan(request);

    public async Task<ConversionResult> ExecuteAsync(
        ConversionPlan plan, IProgress<ConversionProgress> progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = await plan.Engine.ConvertAsync(plan.Request, progress, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (result.IsSimulation != plan.Engine.IsSimulation)
            throw new InvalidOperationException("The engine returned an unexpected result type.");
        return result;
    }
}
