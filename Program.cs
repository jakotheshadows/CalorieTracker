using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using CalorieTracker;
using CalorieTracker.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddScoped(sp => new HttpClient());
builder.Services.AddScoped<LocalStore>();
builder.Services.AddScoped<AppState>();
builder.Services.AddScoped<UsdaService>();

await builder.Build().RunAsync();
