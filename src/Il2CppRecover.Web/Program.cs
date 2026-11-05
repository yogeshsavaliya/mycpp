using Microsoft.AspNetCore.Http.Features;

namespace Il2CppRecover.Web;

public partial class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Services.Configure<FormOptions>(options => options.MultipartBodyLengthLimit = 2L * 1024 * 1024 * 1024);
        builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 2L * 1024 * 1024 * 1024);

        var studio = StudioOptions.From(builder.Configuration, builder.Environment);
        if (studio.FixedUrl)
            builder.WebHost.UseUrls(studio.Url);

        builder.Services.AddSingleton(studio);
        builder.Services.AddSingleton<StudioDatabase>();
        builder.Services.AddSingleton<IAnalysisProcess, CliAnalysisProcess>();
        builder.Services.AddSingleton<JobRunner>();
        builder.Services.AddHostedService(provider => provider.GetRequiredService<JobRunner>());

        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            if (HttpMethods.IsPost(context.Request.Method) || HttpMethods.IsPut(context.Request.Method) || HttpMethods.IsDelete(context.Request.Method))
            {
                if (!StudioApi.OriginAllowed(context.Request))
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    await context.Response.WriteAsJsonAsync(new { error = "This local action was refused because the request origin is not this computer." });
                    return;
                }
                var header = context.Request.Headers["X-Studio-Token"].ToString();
                var cookie = context.Request.Cookies["studio.token"];
                if (string.IsNullOrEmpty(header) || !string.Equals(header, cookie, StringComparison.Ordinal))
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    await context.Response.WriteAsJsonAsync(new { error = "This action needs the local session token. Refresh the page and try again." });
                    return;
                }
            }
            await next();
        });

        if (Directory.Exists(studio.FrontendDist))
        {
            var files = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(studio.FrontendDist);
            app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
            app.UseStaticFiles(new StaticFileOptions { FileProvider = files });
        }

        StudioApi.Map(app);

        app.MapFallback(async context =>
        {
            if (context.Request.Path.StartsWithSegments("/api"))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                await context.Response.WriteAsJsonAsync(new { error = "That action is not part of this application." });
                return;
            }
            if (!Directory.Exists(studio.FrontendDist))
            {
                context.Response.ContentType = "text/plain";
                await context.Response.WriteAsync("The web interface has not been built yet. From the repository, run scripts/start-studio.sh, or scripts\\start-studio.ps1 on Windows, after installing the .NET SDK and Node.js.");
                return;
            }
            var index = Path.Combine(studio.FrontendDist, "index.html");
            context.Response.ContentType = "text/html";
            await context.Response.SendFileAsync(index);
        });

        app.Lifetime.ApplicationStarted.Register(() =>
        {
            if (!studio.FixedUrl)
                return;
            Console.WriteLine("IL2CPP Recovery Studio is ready at " + studio.Url);
            Console.WriteLine("Projects are stored in " + studio.DataRoot);
            Console.WriteLine("Press Ctrl+C to stop. Stored projects stay on this computer.");
        });

        app.Run();
    }
}
