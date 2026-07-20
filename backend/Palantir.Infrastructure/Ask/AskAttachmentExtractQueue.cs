using System.Threading.Channels;
using Palantir.Application.Ask;

namespace Palantir.Infrastructure.Ask;

public sealed class AskAttachmentExtractQueue : IAskAttachmentExtractQueue
{
    private readonly Channel<AskAttachmentExtractJob> _channel =
        Channel.CreateUnbounded<AskAttachmentExtractJob>(
            new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false
            });

    public ValueTask EnqueueAsync(AskAttachmentExtractJob job, CancellationToken cancellationToken = default) =>
        _channel.Writer.WriteAsync(job, cancellationToken);

    public IAsyncEnumerable<AskAttachmentExtractJob> DequeueAllAsync(CancellationToken cancellationToken) =>
        _channel.Reader.ReadAllAsync(cancellationToken);
}
