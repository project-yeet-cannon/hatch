# Modules

A module is one family app: its own folder, its own Postgres schema, its own
migration history. It rides the existing pod, deploy, backup, and log pipeline —
adding one touches no container, manifest, CI job, or `Program.cs`.

Not every module is the family's. Hatch
([`docs/hatch.md`](../../../docs/hatch.md)) is the operator's, on its own host
and behind the admin gate, and it is the worked example for the two things a
module of that kind needs: a surface only an operator reaches, and a caller that
is a program rather than a browser.

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
   the assembly. A module the household uses needs nothing more; one that is the
   operator's carries [`RequireAdmin`](../Common/RequireAdminAttribute.cs), and
   naming a scope on it (`[RequireAdmin(AcceptScope = …)]`) is what opens a
   route to an API key. Hatch is the worked example of both —
   [`docs/hatch.md`](../../../docs/hatch.md), "The wall, the admin gate, and API
   keys".

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

  **A person is an authorization input**, and scoping rows to one is ordinary —
  Quill does it ([`docs/quill.md`](../../../docs/quill.md)), and sharing a note
  with a named person, read or write, is where that goes. What does not exist
  yet is a permission *model*, so a module doing this owns the shape of its own
  answer. Do it the way Quill does:

  - The person is **a clause in the query**, never a check after the rows are
    loaded. `Where(n => n.PersonId == me)` cannot be forgotten by a code path
    the way an `if` can, and a mistake in it is an empty result rather than a
    leak.
  - "Who may read this" is **one expression, in one place**. When it grows from
    ownership to sharing it widens; it must not sprout a second copy beside it.
  - Every refusal is **the same blank `404`** — no person, someone else's row,
    and no such row alike. A `403` confirms that an id names a real row
    belonging to a real person.
  - Ask who is calling through `ICallerIdentity` and nothing else.

  Two things are still nobody's to invent in a module folder. **`IsAdmin` is
  read in one place and it is not here** — `Services/Auth/AdminGate.cs`, guarding
  the operator's own tools, and a module reaching for a household-wide role is
  a module answering a question about the house rather than about its own rows.
  Hatch is entirely the operator's and still reads it that way: every controller
  carries the attribute, and the gate answers ([`docs/hatch.md`](../../../docs/hatch.md)).
  And no endpoint changes *what a verb does* based on who is asking; that is the
  permission model arriving, and it should arrive on purpose rather than as one
  module's `if`.
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
