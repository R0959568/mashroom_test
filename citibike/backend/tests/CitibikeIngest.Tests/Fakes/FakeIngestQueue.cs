using System.Threading.Channels;
using CitibikeIngest.Api.Infrastructure;

namespace CitibikeIngest.Tests.Fakes;

public sealed class FakeIngestQueue : IIngestQueue
{
    private readonly Channel<long> channel = Channel.CreateUnbounded<long>();

    public List<long> Enqueued { get; } = [];

    public void Enqueue(long runId)
    {
        Enqueued.Add(runId);
        channel.Writer.TryWrite(runId);
    }

    public ChannelReader<long> Reader => channel.Reader;
}
