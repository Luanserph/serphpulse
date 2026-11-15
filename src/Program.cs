using System.Diagnostics;
using System.Reflection;
using Microsoft.Extensions.FileProviders;
using OtimizadorJogos.Services;

// Usa exclusivamente o diretório do próprio executável SerphPulse (nunca o diretório pai)
string baseDir = AppContext.BaseDirectory;
string? wwwrootPath = null;
string[] candidatos =
{
    Path.Combine(baseDir, "wwwroot"),
    Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "wwwroot"))
};
foreach (var c in candidatos)
{
    if (Directory.Exists(c) && File.Exists(Path.Combine(c, "index.html")))
    {
        wwwrootPath = c;
        break;
    }
}

var options = new WebApplicationOptions
{
    Args = args,
    ContentRootPath = baseDir,
    WebRootPath = wwwrootPath ?? Path.Combine(baseDir, "wwwroot")
};

var builder = WebApplication.CreateBuilder(options);

builder.Services.AddSingleton<LicenseService>();
builder.Services.AddSingleton<HardwareMonitorService>();
builder.Services.AddSingleton<SystemToolsService>();
builder.Services.AddControllers();

var app = builder.Build();

// Permite conexão com painel/API hospedado na Vercel (CORS + Private Network Access)
app.Use(async (ctx, next) =>
{
    ctx.Response.Headers["Access-Control-Allow-Origin"] = "*";
    ctx.Response.Headers["Access-Control-Allow-Methods"] = "GET, POST, OPTIONS";
    ctx.Response.Headers["Access-Control-Allow-Headers"] = "Content-Type, Authorization, X-Serph-Hwid";
    ctx.Response.Headers["Access-Control-Allow-Private-Network"] = "true";
    if (ctx.Request.Method == "OPTIONS")
    {
        ctx.Response.StatusCode = 204;
        return;
    }
    await next();
});

// Rota principal "/" servindo sempre o index.html do SerphPulse sem cache antigo do navegador
app.MapGet("/", async context =>
{
    context.Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate, max-age=0";
    context.Response.Headers["Pragma"] = "no-cache";

    var asm = Assembly.GetExecutingAssembly();
    var resName = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("index.html", StringComparison.OrdinalIgnoreCase));
    if (resName != null)
    {
        using var stream = asm.GetManifestResourceStream(resName);
        if (stream != null)
        {
            context.Response.ContentType = "text/html; charset=utf-8";
            await stream.CopyToAsync(context.Response.Body);
            return;
        }
    }

    if (wwwrootPath != null)
    {
        string idxFile = Path.Combine(wwwrootPath, "index.html");
        if (File.Exists(idxFile))
        {
            context.Response.ContentType = "text/html; charset=utf-8";
            await context.Response.SendFileAsync(idxFile);
            return;
        }
    }

    context.Response.StatusCode = 404;
    await context.Response.WriteAsync("index.html nao encontrado.");
});

if (wwwrootPath != null && Directory.Exists(wwwrootPath))
{
    var provider = new PhysicalFileProvider(wwwrootPath);
    app.UseStaticFiles(new StaticFileOptions { FileProvider = provider });
}

app.MapControllers();

int chosenPort = 8080;
for (int p = 8080; p <= 8095; p++)
{
    try
    {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, p);
        listener.Start();
        listener.Stop();
        chosenPort = p;
        break;
    }
    catch
    {
        // Se a porta 8080 já for o próprio SerphPulse rodando, apenas abre o navegador nela e encerra amigavelmente
        if (p == 8080 && !args.Contains("--no-browser", StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(1.5) };
                var resp = await http.GetStringAsync("http://127.0.0.1:8080/api/licenca/status");
                if (resp.Contains("hwid", StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine("SerphPulse ja esta em execucao em http://127.0.0.1:8080 — abrindo navegador...");
                    Process.Start(new ProcessStartInfo { FileName = "http://127.0.0.1:8080", UseShellExecute = true });
                    return;
                }
            }
            catch { }
        }
    }
}

var url = $"http://127.0.0.1:{chosenPort}";
Console.WriteLine($"====================================================");
Console.WriteLine($"  SERPHPULSE ATIVO EM: {url}");
Console.WriteLine($"  Mantenha esta janela aberta enquanto usa o painel.");
Console.WriteLine($"====================================================");

if (!args.Contains("--no-browser", StringComparer.OrdinalIgnoreCase))
{
    app.Lifetime.ApplicationStarted.Register(() =>
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Não foi possível abrir o navegador automaticamente: {ex.Message}");
        }
    });
}

app.Run(url);
