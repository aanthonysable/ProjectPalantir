using System.Threading.Channels;
using Palantir.Application.Knowledge;

namespace Palantir.Infrastructure.Knowledge;

public sealed class KnowledgeIndexQueue : IKnowledgeIndexQueue
{
    private readonly Channel<KnowledgeIndexJob> _channel = Channel.CreateUnbounded<KnowledgeIndexJob>(
        new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

    public ValueTask EnqueueAsync(KnowledgeIndexJob job, CancellationToken cancellationToken = default) =>
        _channel.Writer.WriteAsync(job, cancellationToken);

    public IAsyncEnumerable<KnowledgeIndexJob> DequeueAllAsync(CancellationToken cancellationToken) =>
        _channel.Reader.ReadAllAsync(cancellationToken);
}
