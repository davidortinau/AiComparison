using System.Threading.Channels;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;

namespace AiComparison.Services.Maf;

/// <summary>
/// Shared driver for the hybrid two-stage Agent Framework pipeline:
/// <c>[preprocess executor] --(ChatMessage + TurnToken)--> [cloud ChatClientAgent] --> streamed output</c>.
///
/// The preprocess executor performs the local stage (chunk map or PII anonymization), writes
/// human-readable progress markers directly to <paramref name="output"/>, then forwards a
/// <see cref="ChatMessage"/> plus a <see cref="TurnToken"/> to the cloud agent. This driver watches
/// the workflow event stream and forwards the cloud agent's streamed tokens to the same channel so
/// the service can surface a single ordered <see cref="IAsyncEnumerable{T}"/> of strings.
///
/// The cloud agent is the only node that emits <see cref="AgentResponseUpdateEvent"/>, so its
/// streamed tokens are unambiguous without filtering by executor id.
/// </summary>
internal static class MafPipeline
{
    public static async Task RunAsync(
        Executor preprocessExecutor,
        AIAgent cloudAgent,
        string input,
        ChannelWriter<string> output,
        Action<string> onCloudToken,
        CancellationToken cancellationToken)
    {
        Exception? error = null;
        try
        {
            var preprocess = ExecutorBindingExtensions.BindExecutor(preprocessExecutor);
            var cloud = ExecutorBindingExtensions.BindAsExecutor(cloudAgent, emitEvents: true);

            var workflow = new WorkflowBuilder(preprocess)
                .AddEdge(preprocess, cloud)
                .WithOutputFrom(cloud)
                .Build(validateOrphans: false);

            await using var run = await InProcessExecution.RunStreamingAsync(
                workflow, input, cancellationToken: cancellationToken);

            await foreach (var evt in run.WatchStreamAsync(cancellationToken))
            {
                switch (evt)
                {
                    case AgentResponseUpdateEvent update when !string.IsNullOrEmpty(update.Update?.Text):
                        var token = update.Update!.Text!;
                        onCloudToken(token);
                        await output.WriteAsync(token, cancellationToken);
                        break;

                    case WorkflowErrorEvent failure:
                        error = failure.Exception ?? new InvalidOperationException("Workflow failed.");
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            error = ex;
        }
        finally
        {
            output.TryComplete(error);
        }
    }
}
