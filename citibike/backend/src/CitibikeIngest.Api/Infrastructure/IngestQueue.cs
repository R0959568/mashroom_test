using System.Threading.Channels;

namespace CitibikeIngest.Api.Infrastructure;

/// <summary>Hands run ids from the request path to the background worker. Run state itself lives in Postgres.</summary>
public interface IIngestQueue
{
    void Enqueue(long runId);

    ChannelReader<long> Reader { get; }
}

public sealed class IngestQueue : IIngestQueue
{
    private readonly Channel<long> channel = Channel.CreateUnbounded<long>(new UnboundedChannelOptions { SingleReader = true });

    public void Enqueue(long runId)
    {
        if (!channel.Writer.TryWrite(runId))
        {
            throw new InvalidOperationException("The ingest queue is closed");
        }
    }

    public ChannelReader<long> Reader => channel.Reader;
}
