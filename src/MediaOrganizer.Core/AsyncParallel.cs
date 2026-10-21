using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace MediaOrganizer.Core;

internal static class AsyncParallel
{
    /// <summary>
    /// Processa os itens com no máximo <paramref name="degree"/> tarefas simultâneas e devolve cada resultado
    /// assim que fica pronto (fora de ordem), para a interface ir exibindo o progresso.
    /// </summary>
    public static async IAsyncEnumerable<TOut> StreamAsync<TIn, TOut>(
        IEnumerable<TIn> items, int degree, Func<TIn, CancellationToken, Task<TOut>> process,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var channel = Channel.CreateUnbounded<TOut>();
        var producer = Task.Run(async () =>
        {
            try
            {
                var options = new ParallelOptions { MaxDegreeOfParallelism = degree, CancellationToken = ct };
                await Parallel.ForEachAsync(items, options, async (item, token) =>
                    await channel.Writer.WriteAsync(await process(item, token).ConfigureAwait(false), token).ConfigureAwait(false))
                    .ConfigureAwait(false);
                channel.Writer.Complete();
            }
            catch (Exception e)
            {
                channel.Writer.Complete(e);
            }
        }, ct);

        await foreach (var result in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false)) yield return result;
        await producer.ConfigureAwait(false);
    }
}
