using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Weave.Dashboard.Approvals;

public static class ExtensionsToApprovalReview
{
    public static IServiceCollection AddApprovalReviewScreen(this IServiceCollection services, IConfiguration configuration)
    {
        var options = ApprovalReviewOptions.Read(configuration);
        services.AddSingleton(options);
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped(provider => new ApprovalReviewSession(new HttpClient(ApprovalReviewOptions.CreateHandler())
        {
            BaseAddress = options.Upstream,
            Timeout = TimeSpan.FromSeconds(30)
        }, provider.GetRequiredService<TimeProvider>()));
        return services;
    }
}
