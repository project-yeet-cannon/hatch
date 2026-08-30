# Modules

A module is one family app: its own folder, its own Postgres schema, its own
migration history. It rides the existing pod, deploy, backup, and log pipeline —
adding one touches no container, manifest, CI job, or `Program.cs`.

## Adding a module

1. `mkdir Modules/<Name>/` and write the entities and context there.
2. Context: pin the schema and implement the marker interface.

   ```csharp
   public class WidgetContext(DbContextOptions<WidgetContext> options)
       : DbContext(options), IModuleContext
   {
       public const string Schema = "widget";
       public DbSet<Widget> Widgets => Set<Widget>();

       protected override void OnModelCreating(ModelBuilder b) => b.HasDefaultSchema(Schema);
   }
   ```

3. Design-time factory, so `dotnet ef` can build the context without running the app:

   ```csharp
   public class WidgetDesignTimeFactory : ModuleDesignTimeFactory<WidgetContext>
   {
       protected override string Schema => WidgetContext.Schema;
   }
   ```

4. Give the module one DI entry point, so its services live in its own folder
   and the registry stays a table of contents:

   ```csharp
   public static class WidgetModule
   {
       public static IServiceCollection AddWidgetModule(this IServiceCollection services, IConfiguration configuration)
       {
           services.AddModuleContext<WidgetContext>(configuration, WidgetContext.Schema);
           services.AddScoped<IWidgetService, WidgetService>();
           return services;
       }
   }
   ```

   Then one line in `AddAerieModules` in
   [`ModuleRegistration.cs`](ModuleRegistration.cs):

   ```csharp
   services.AddWidgetModule(configuration);
   ```

5. Scaffold the first migration into the module folder:

   ```sh
   dotnet ef migrations add Init --context WidgetContext \
     --project ./src/Aerie.Api/Aerie.Api.csproj -o Modules/Widget/Migrations
   ```

6. Controller at `/api/<name>/*` — `AddControllers` already finds it anywhere in
   the assembly.

Startup migrates every registered module context automatically. Nothing else to
wire.

## Rules

- **One schema per module, one database.** Cross-app reads are plain SQL and one
  CNPG backup still covers everything. Splitting a module into its own service
  later is a connection string change, not a rewrite.
- **Never reference another module's entities.** Read its tables through SQL or
  its service if you must; a compile-time dependency turns two modules into one.
- **`Schema` is declared once**, as the `const` on the context, and referenced by
  the registration and the design-time factory. Three copies of the string is how
  a module ends up with its history table in the wrong schema.
- **A module registers its own services**, via one `Add<Name>Module` extension.
  `AddAerieModules` gains exactly one line per app and never grows a section.
- **No module invents a user.** The wall
  ([`docs/auth-architecture.md`](../../../docs/auth-architecture.md)) still
  authenticates a *device*, not a person. There is now a `People` table, and it
  is in the core `public` schema for exactly this reason: a module-owned one
  would make every module depend on one module. Ask who is calling through
  [`ICallerIdentity`](../Services/Auth/CallerIdentity.cs) — one constructor
  parameter, resolved once per request — and never by reading the cookie for
  yourself.

  **A person may decide what a caller can reach. A person never decides what a
  caller is allowed to do.** Quill is the one module that reads one
  ([`docs/quill.md`](../../../docs/quill.md)): a note belongs to a person, so
  `PersonId` is the first clause of every query in the module. That is
  ownership, enforced by a `WHERE` clause rather than by a rule a code path
  could forget to consult, and it is the whole of the concession.

  Everything on the other side of that line is unchanged and stays that way.
  **Nothing reads `IsAdmin`.** No endpoint behaves differently for one person
  than for another. There is no role, no scope, and no permission table — a
  module that starts gating behaviour on a person is inventing a permission
  model in a corner rather than adding a column, and the lockout path in
  `docs/auth-architecture.md` has to be designed first.

  If a module does scope rows to a person, do it the way Quill does: the person
  is a clause in the query, not a check after the load, and every refusal is the
  same blank `404`. A `403` confirms that the id names a real row belonging to a
  real person, which is the fact a private row has to keep.
- **Nothing operator-specific in module code** — domains, hostnames, and paths
  come from config, per [`docs/ethos.md`](../../../docs/ethos.md). The install's
  own public URL is already solved: [`AppsOptions`](AppsOptions.cs)
  (`Apps:PublicBaseUrl`, served to the shell by `GET /api/apps/config`). Read it
  from there rather than adding a second setting for the same fact.

## What's shared, and what isn't

Home-automation code (`Controllers/`, `Services/`, `Ef/`) is layer-first because
it's one domain sliced by layer. Modules are module-first because the app *is*
the boundary. Both layouts are intentional; don't migrate one to the other.

`Ef/AerieContext.cs` and its `public` schema belong to home automation. A module
migration must never appear in `Migrations/` or in
`public.__EFMigrationsHistory`.

DTO names are yours to reuse: Storage and Gather both have an `ItemDto`, and the
OpenAPI document qualifies a module's schemas with its folder name so the second
one doesn't take Swagger down for the whole app - see
[`Common/SwaggerSchemaIds.cs`](../Common/SwaggerSchemaIds.cs).
