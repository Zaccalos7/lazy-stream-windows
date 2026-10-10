using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.OpenApi;
using Microsoft.OpenApi.Extensions;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.Swagger;
using Orbis.Stream.Core.Configuration;
using Orbis.Stream.Core.Contracts;
using Orbis.Stream.Core.Http;
using Orbis.Stream.Core.I18n;
using Orbis.Stream.Core.Logging;

namespace Orbis.Stream.Core.Hosting;

/// <summary>
/// Wires the Kestrel host, the Razor pages and their static resources, the exception middleware and the OpenAPI
/// document at the same paths the Spring application exposed.
/// </summary>
public static class OrbisWebApplicationExtensions
{
    public const string OpenApiDocumentPath = "/documentazione";
    public const string OpenApiDocumentName = "v1";
    public const string SwaggerUiPath = "/swagger-ui.html";

    /// <summary>Spring Boot <c>server.port</c>: every interface, so other devices can use the app too.</summary>
    public const string BindAllInterfacesPrefix = "http://*";

    public static WebApplication BuildOrbisWebApplication(
        this WebApplicationBuilder builder,
        OrbisRuntimeOptions options)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(options);

        builder.WebHost.UseUrls($"{BindAllInterfacesPrefix}:{options.Port}");
        ConfigureLogging(builder, options);

        builder.Services.AddOrbisStream(options);
        builder.Services.AddSingleton<RequestValidator>();
        builder.Services.AddSingleton<UiText>();
        builder.Services.AddSingleton(ManualText.Instance);
        // The pages live in this library, not in the entry assembly (the WPF shell or the tests).
        builder.Services.AddRazorPages().AddApplicationPart(typeof(Pages.OrbisPageModel).Assembly);
        builder.Services.AddEndpointsApiExplorer();
        // The Java CorsConfiguration allowed any origin, method and header.
        builder.Services.AddCors();
        builder.Services.AddSwaggerGen(swagger => swagger.SwaggerDoc(OpenApiDocumentName, new OpenApiInfo
        {
            Title = "Stream",
            Version = "OPENAPI_3_0"
        }));

        builder.Services.ConfigureHttpJsonOptions(json =>
        {
            json.SerializerOptions.Converters.Add(new LocalDateTimeConverter());
            json.SerializerOptions.Converters.Add(new NullableLocalDateTimeConverter());
            json.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.Never;
        });

        var app = builder.Build();
        ConfigurePipeline(app, options);
        return app;
    }

    /// <summary>
    /// The log file of the Java configuration (<c>logging.file.name</c>), in the logs directory of
    /// the data directory. ASP.NET Core has no file logger of its own, and the window has no
    /// console, so without it only the warnings reached the Windows event log.
    /// <para>The hosting and routing lines of every request stay out: the preview asks for a
    /// frame thirty times a second, and two lines a request would bury everything else.</para>
    /// </summary>
    private static void ConfigureLogging(WebApplicationBuilder builder, OrbisRuntimeOptions options)
    {
        builder.Logging.SetMinimumLevel(LogLevel.Information);
        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);

        // Registered with a factory so the container disposes it, and what is queued is written.
        builder.Services.AddSingleton<ILoggerProvider>(_ => new FileLoggerProvider(options.LogDirectory));
    }

    private static void ConfigurePipeline(WebApplication app, OrbisRuntimeOptions options)
    {
        app.Environment.WebRootFileProvider = new PhysicalFileProvider(options.WebRootPath);

        app.UseMiddleware<ExceptionHandlingMiddleware>();

        app.UseCors(cors => cors
            .AllowAnyOrigin()
            .AllowAnyMethod()
            .AllowAnyHeader());

        // The stylesheet and the script are small and change with every release: always revalidate.
        app.UseStaticFiles(new StaticFileOptions
        {
            ServeUnknownFileTypes = false,
            OnPrepareResponse = context => context.Context.Response.Headers.CacheControl = "no-cache"
        });

        // springdoc served the document at /documentazione and the UI at /swagger-ui.html. The
        // document is written by an endpoint because the Swashbuckle middleware only answers when
        // the route template contains the {documentName} placeholder.
        app.MapGet(OpenApiDocumentPath, (ISwaggerProvider provider) => Results.Text(
            provider.GetSwagger(OpenApiDocumentName).SerializeAsJson(OpenApiSpecVersion.OpenApi3_0),
            "application/json"));
        app.UseSwaggerUI(ui =>
        {
            ui.SwaggerEndpoint(OpenApiDocumentPath, "Stream");
            ui.RoutePrefix = "swagger-ui";
        });

        app.MapGet(SwaggerUiPath, () => Results.Redirect("/swagger-ui/index.html"));

        app.MapOrbisEndpoints();

        // The pages keep the paths of the React router (basename /orbis).
        app.MapGet("/", () => Results.Redirect("/orbis/mainMenu"));
        app.MapGet("/orbis", () => Results.Redirect("/orbis/mainMenu"));
        app.MapGet("/orbis/lang/{code}", (string code, HttpContext context) =>
        {
            if (UiText.IsSupported(code))
            {
                context.Response.Cookies.Append(Localizer.LanguageCookie, code, new CookieOptions
                {
                    Expires = DateTimeOffset.UtcNow.AddYears(10),
                    SameSite = SameSiteMode.Lax,
                    HttpOnly = true
                });
            }

            // Back to the page that asked, never to another host.
            var back = Uri.TryCreate(context.Request.Headers.Referer.ToString(), UriKind.Absolute, out var uri)
                ? uri.PathAndQuery
                : string.Empty;
            return Results.Redirect(back.StartsWith("/orbis/", StringComparison.Ordinal) ? back : "/orbis/mainMenu");
        });
        app.MapRazorPages();
    }
}
