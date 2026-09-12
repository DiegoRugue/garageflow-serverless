using Amazon.Lambda.Core;

namespace GarageFlow.Serverless.Host;

internal static class InvocationDeadline
{
    private static readonly TimeSpan ResponseSafetyMargin = TimeSpan.FromSeconds(1);

    public static CancellationTokenSource? Start(ILambdaContext? context)
    {
        if (context is null)
        {
            return null;
        }

        var dependencyBudget = context.RemainingTime - ResponseSafetyMargin;
        return dependencyBudget > TimeSpan.Zero
            ? new CancellationTokenSource(dependencyBudget)
            : null;
    }
}
