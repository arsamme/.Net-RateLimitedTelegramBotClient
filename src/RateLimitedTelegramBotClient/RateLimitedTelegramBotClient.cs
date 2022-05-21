using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Telegram.Bot;
using Telegram.Bot.Requests.Abstractions;

namespace Arsam.RateLimitedTelegramBotClient;

public class RateLimitedTelegramBotClient : TelegramBotClient
{
    private readonly TelegramRequestScheduler _requestScheduler;

    public RateLimitedTelegramBotClient(TelegramBotClientOptions options, SchedulerSettings? schedulerSettings = null,
        HttpClient? httpClient = null) : base(options,
        httpClient)
    {
        _requestScheduler = new TelegramRequestScheduler(schedulerSettings);
    }

    public RateLimitedTelegramBotClient(string token, SchedulerSettings? schedulerSettings = null,
        HttpClient? httpClient = null) : base(token, httpClient)
    {
        _requestScheduler = new TelegramRequestScheduler(schedulerSettings);
    }


    public override async Task<TResponse> MakeRequestAsync<TResponse>(IRequest<TResponse> request,
        CancellationToken cancellationToken = new())
    {
        switch (request)
        {
            case IUserTargetable rq:
                await _requestScheduler.YieldAsync(rq.UserId).ConfigureAwait(false);
                break;
            case IChatTargetable rq:
                await _requestScheduler.YieldAsync(rq.ChatId).ConfigureAwait(false);
                break;
        }

        return await base.MakeRequestAsync(request, cancellationToken);
    }
}