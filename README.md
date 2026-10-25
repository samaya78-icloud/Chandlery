# Nimbus — SaaS Dashboard Starter

A Blazor Web App (.NET 10, Interactive Server render mode) with a Tailwind CSS v4 design system: sidebar navigation, stat cards, a revenue chart, a searchable customer table, and a settings page.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download) or later
- [Node.js](https://nodejs.org) 18+ (for the Tailwind CLI)

## Setup

```bash
# 1. Restore .NET dependencies
dotnet restore

# 2. Install Tailwind CLI
npm install

# 3. Build the CSS (one-time)
npm run css:build

# 4. Run the app
dotnet run
```

Then open the URL shown in the console (typically `https://localhost:5001`).

## Development workflow

Run these in two terminals so styles rebuild as you edit `.razor` files:

```bash
npm run css:watch
dotnet watch run
```

## Project structure

```
Components/
  Layout/
    MainLayout.razor   # Sidebar + top bar shell
    NavMenu.razor       # Sidebar navigation links
  Pages/
    Home.razor           # Overview dashboard
    Customers.razor      # Searchable customer table
    Analytics.razor      # Placeholder for charts
    Settings.razor        # Workspace settings form
  App.razor              # Root HTML document
  Routes.razor            # Router
wwwroot/css/
  input.css                # Tailwind entry point (edit theme tokens here)
  app.css                  # Generated output — do not edit directly
```

## Next steps

- Replace the in-memory sample data in `Home.razor` / `Customers.razor` with real API/database calls (e.g. via `HttpClient` or EF Core).
- Add authentication with ASP.NET Core Identity or an external provider.
- If you want offline-capable, client-heavy interactivity for specific pages, add a `.Client` project and switch those pages to `@rendermode InteractiveAuto`.
- Consider [MudBlazor](https://mudblazor.com) or [Radzen Blazor](https://blazor.radzen.com) if you want richer pre-built components (data grids, charts, date pickers) layered on top of this Tailwind foundation.
