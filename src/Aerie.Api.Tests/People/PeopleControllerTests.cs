using Aerie.Api.Common;
using Aerie.Api.Controllers;
using Aerie.Api.Ef;
using Aerie.Api.Models.People;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

namespace Aerie.Api.Tests.People;

/// <summary>
/// People CRUD, and the two things about it that are not ordinary CRUD: a photo
/// arrives as a request body rather than as a field, and deleting a person must
/// not delete anything a device needs to get in.
/// </summary>
public class PeopleControllerTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x01];

    [Fact]
    public async Task StartsEmpty()
    {
        var (controller, _, _) = NewController();

        Assert.Empty(await controller.GetAll(CancellationToken.None));
    }

    [Fact]
    public async Task CreatesAPersonWithANormalizedName()
    {
        var (controller, db, _) = NewController();

        var created = await controller.Create(new PersonWriteRequest("   Ada    Lovelace  "), CancellationToken.None);

        var dto = Assert.IsType<PersonDto>(Assert.IsType<CreatedAtActionResult>(created.Result).Value);
        Assert.Equal("Ada Lovelace", dto.Name);
        Assert.False(dto.HasPhoto);
        Assert.Equal(0, dto.SessionCount);
        // The stored row is the normalized one, not the typed one - the client
        // is not the thing that decides what a name is.
        Assert.Equal("Ada Lovelace", (await db.People.SingleAsync()).Name);
    }

    [Fact]
    public async Task CarriesTheAdminFlagWithoutEnforcingIt()
    {
        // Deliberately a column and an input with no branch behind it. The
        // reason it exists before the thing that reads it is on EfPerson.
        var (controller, db, _) = NewController();

        var created = await controller.Create(new PersonWriteRequest("Ada", IsAdmin: true), CancellationToken.None);

        Assert.True(Assert.IsType<PersonDto>(Assert.IsType<CreatedAtActionResult>(created.Result).Value).IsAdmin);
        Assert.True((await db.People.SingleAsync()).IsAdmin);
    }

    [Fact]
    public async Task DefaultsToNotAnAdministrator()
    {
        // The omission case, which is the one that matters: a caller who only
        // meant to name someone must not promote them by leaving a field out.
        var (controller, _, _) = NewController();

        await controller.Create(new PersonWriteRequest("Ada"), CancellationToken.None);

        Assert.False((await controller.GetAll(CancellationToken.None)).Single().IsAdmin);
    }

    [Fact]
    public async Task TogglesTheAdminFlagBothWays()
    {
        var (controller, _, _) = NewController();
        var id = await Create(controller, "Ada");

        await controller.Update(id, new PersonWriteRequest("Ada", IsAdmin: true), CancellationToken.None);
        Assert.True((await controller.GetAll(CancellationToken.None)).Single().IsAdmin);

        // The half that a "set it if true" implementation would silently drop.
        await controller.Update(id, new PersonWriteRequest("Ada", IsAdmin: false), CancellationToken.None);
        Assert.False((await controller.GetAll(CancellationToken.None)).Single().IsAdmin);
    }

    [Fact]
    public async Task RefusesANameThePersonNameRulesRefuse()
    {
        var (controller, db, _) = NewController();

        var created = await controller.Create(new PersonWriteRequest("   "), CancellationToken.None);

        Assert.Equal(PersonName.EmptyError, Assert.IsType<BadRequestObjectResult>(created.Result).Value);
        Assert.Empty(db.People);
    }

    [Fact]
    public async Task LetsTwoPeopleShareAName()
    {
        // A household contains a Sam and a Sam. There is deliberately no unique
        // index behind this, and the id is what anything actually keys on.
        var (controller, _, _) = NewController();

        await controller.Create(new PersonWriteRequest("Sam"), CancellationToken.None);
        var second = await controller.Create(new PersonWriteRequest("Sam"), CancellationToken.None);

        Assert.IsType<CreatedAtActionResult>(second.Result);
        Assert.Equal(2, (await controller.GetAll(CancellationToken.None)).Count);
    }

    [Fact]
    public async Task ListsByNameRatherThanByCreationOrder()
    {
        var (controller, _, _) = NewController();

        foreach (var name in new[] { "Zoe", "Adam", "Mia" })
        {
            await controller.Create(new PersonWriteRequest(name), CancellationToken.None);
        }

        Assert.Equal(["Adam", "Mia", "Zoe"], (await controller.GetAll(CancellationToken.None)).Select(p => p.Name));
    }

    [Fact]
    public async Task RenamingMovesUpdatedAtAndLeavesCreatedAtAlone()
    {
        var (controller, _, time) = NewController();
        var id = await Create(controller, "Ada");

        time.Advance(TimeSpan.FromDays(1));
        var updated = await controller.Update(id, new PersonWriteRequest("Ada Lovelace"), CancellationToken.None);

        var dto = Assert.IsType<PersonDto>(updated.Value);
        Assert.Equal("Ada Lovelace", dto.Name);
        Assert.Equal(Now, dto.CreatedAt);
        Assert.Equal(Now.AddDays(1), dto.UpdatedAt);
    }

    [Fact]
    public async Task RefusesToRenameSomeoneWhoIsNotThere()
    {
        var (controller, _, _) = NewController();

        var updated = await controller.Update(Guid.NewGuid(), new PersonWriteRequest("Ada"), CancellationToken.None);

        Assert.IsType<NotFoundResult>(updated.Result);
    }

    [Fact]
    public async Task CountsThePersonsSessions()
    {
        var (controller, db, _) = NewController();
        var id = await Create(controller, "Ada");

        db.AuthGrants.Add(NewGrant("Ada's iPhone", id));
        db.AuthGrants.Add(NewGrant("Kitchen tablet", personId: null));
        await db.SaveChangesAsync();

        var people = await controller.GetAll(CancellationToken.None);

        Assert.Equal(1, Assert.Single(people).SessionCount);
    }

    [Fact]
    public async Task ListsThePersonsSessionsNewestFirst()
    {
        var (controller, db, time) = NewController();
        var id = await Create(controller, "Ada");

        db.AuthGrants.Add(NewGrant("Old phone", id));
        time.Advance(TimeSpan.FromDays(30));
        db.AuthGrants.Add(NewGrant("New phone", id, time.GetUtcNow()));
        db.AuthGrants.Add(NewGrant("Someone else's", personId: null, time.GetUtcNow()));
        await db.SaveChangesAsync();

        var sessions = await controller.GetSessions(id, CancellationToken.None);

        Assert.Equal(["New phone", "Old phone"], Assert.IsAssignableFrom<IReadOnlyList<PersonSessionDto>>(sessions.Value).Select(s => s.Label));
    }

    [Fact]
    public async Task DistinguishesAPersonWithNoSessionsFromAPersonWhoIsNotThere()
    {
        // Both are an empty list to a careless implementation, and they are
        // different answers: one means "she has never held a tablet", the other
        // means the page is looking at someone who was deleted.
        var (controller, _, _) = NewController();
        var id = await Create(controller, "Ada");

        Assert.Empty(Assert.IsAssignableFrom<IReadOnlyList<PersonSessionDto>>((await controller.GetSessions(id, CancellationToken.None)).Value));
        Assert.IsType<NotFoundResult>((await controller.GetSessions(Guid.NewGuid(), CancellationToken.None)).Result);
    }

    [Fact]
    public async Task DeletingAPersonDoesNotRevokeTheirDevices()
    {
        // The single most important assertion in this file. Cascading here
        // would turn an administrative tidy-up into a lockout, and the symptom
        // would be a wall tablet that stopped working for no visible reason.
        var (controller, db, _) = NewController();
        var id = await Create(controller, "Ada");
        db.AuthGrants.Add(NewGrant("Ada's iPhone", id));
        await db.SaveChangesAsync();

        Assert.IsType<NoContentResult>(await controller.Delete(id, CancellationToken.None));

        var grant = await db.AuthGrants.SingleAsync();
        Assert.Null(grant.PersonId);
        Assert.Equal("Ada's iPhone", grant.Label);
    }

    [Fact]
    public async Task DeletingAPersonTakesTheirPhotoWithThem()
    {
        var (controller, db, _) = NewController();
        var id = await Create(controller, "Ada");
        await PutPhoto(controller, id, Png);

        await controller.Delete(id, CancellationToken.None);

        Assert.Empty(db.PersonPhotos);
    }

    // ---- Photo ----

    [Fact]
    public async Task StoresAPhotoAndReportsItOnThePerson()
    {
        var (controller, db, _) = NewController();
        var id = await Create(controller, "Ada");

        var result = await PutPhoto(controller, id, Png);

        var dto = Assert.IsType<PersonDto>(result.Value);
        Assert.True(dto.HasPhoto);
        Assert.Equal(Now, dto.PhotoUpdatedAt);

        var photo = await db.PersonPhotos.SingleAsync();
        Assert.Equal(Png, photo.Bytes);
        Assert.Equal("image/png", photo.ContentType);
    }

    [Fact]
    public async Task SniffsTheTypeRatherThanBelievingTheRequest()
    {
        var (controller, db, _) = NewController();
        var id = await Create(controller, "Ada");

        // A caller insisting these bytes are a GIF. They are a PNG, and a PNG
        // is what gets stored and later served.
        await PutPhoto(controller, id, Png, declaredContentType: "image/gif");

        Assert.Equal("image/png", (await db.PersonPhotos.SingleAsync()).ContentType);
    }

    [Fact]
    public async Task RefusesAnUploadThatIsNotAnImage()
    {
        var (controller, db, _) = NewController();
        var id = await Create(controller, "Ada");

        var result = await PutPhoto(controller, id, "<script>alert(1)</script>"u8.ToArray(), declaredContentType: "image/png");

        Assert.Equal(PersonPhoto.UnsupportedError, Assert.IsType<BadRequestObjectResult>(result.Result).Value);
        Assert.Empty(db.PersonPhotos);
    }

    [Fact]
    public async Task ReplacesAPhotoRatherThanAccumulatingThem()
    {
        var (controller, db, time) = NewController();
        var id = await Create(controller, "Ada");
        await PutPhoto(controller, id, Png);

        time.Advance(TimeSpan.FromHours(1));
        byte[] jpeg = [0xFF, 0xD8, 0xFF, 0xE0];
        await PutPhoto(controller, id, jpeg);

        var photo = await db.PersonPhotos.SingleAsync();
        Assert.Equal(jpeg, photo.Bytes);
        Assert.Equal("image/jpeg", photo.ContentType);
        // The upload time is the photo's version, and it is what makes a stable
        // <img src> show the new picture instead of the cached old one.
        Assert.Equal(Now.AddHours(1), photo.UpdatedAt);
    }

    [Fact]
    public async Task ServesThePhotoWithTheTypeItWasSniffedAsAndTellsTheBrowserNotToGuess()
    {
        var (controller, _, _) = NewController();
        var id = await Create(controller, "Ada");
        await PutPhoto(controller, id, Png);

        var result = Assert.IsType<FileContentResult>(await controller.GetPhoto(id, CancellationToken.None));

        Assert.Equal("image/png", result.ContentType);
        Assert.Equal(Png, result.FileContents);
        // The other half of sniffing the type: having decided what this is, the
        // browser is told not to second-guess it either.
        Assert.Equal("nosniff", controller.Response.Headers.XContentTypeOptions);
        Assert.NotNull(result.EntityTag);
    }

    [Fact]
    public async Task HasNoPhotoToServeUntilOneIsUploaded()
    {
        var (controller, _, _) = NewController();
        var id = await Create(controller, "Ada");

        Assert.IsType<NotFoundResult>(await controller.GetPhoto(id, CancellationToken.None));
    }

    [Fact]
    public async Task RemovingAPhotoLeavesThePerson()
    {
        var (controller, db, _) = NewController();
        var id = await Create(controller, "Ada");
        await PutPhoto(controller, id, Png);

        Assert.IsType<NoContentResult>(await controller.DeletePhoto(id, CancellationToken.None));

        Assert.Empty(db.PersonPhotos);
        Assert.Single(db.People);
        Assert.False((await controller.GetAll(CancellationToken.None)).Single().HasPhoto);
    }

    [Fact]
    public async Task RefusesAPhotoForSomeoneWhoIsNotThere()
    {
        var (controller, db, _) = NewController();

        var result = await PutPhoto(controller, Guid.NewGuid(), Png);

        Assert.IsType<NotFoundResult>(result.Result);
        Assert.Empty(db.PersonPhotos);
    }

    // ---- Harness ----

    private static async Task<Guid> Create(PeopleController controller, string name)
    {
        var created = await controller.Create(new PersonWriteRequest(name), CancellationToken.None);
        return ((PersonDto)((CreatedAtActionResult)created.Result!).Value!).Id;
    }

    private static Task<ActionResult<PersonDto>> PutPhoto(
        PeopleController controller,
        Guid id,
        byte[] bytes,
        string declaredContentType = "application/octet-stream")
    {
        controller.ControllerContext.HttpContext.Request.Body = new MemoryStream(bytes);
        controller.ControllerContext.HttpContext.Request.ContentType = declaredContentType;
        return controller.PutPhoto(id, CancellationToken.None);
    }

    private static EfAuthGrant NewGrant(string label, Guid? personId, DateTimeOffset? createdAt = null) => new()
    {
        TokenHash = new byte[32],
        Label = label,
        PersonId = personId,
        Kind = AuthGrantKind.Interactive,
        CreatedAt = createdAt ?? Now,
        CookieIssuedAt = createdAt ?? Now,
    };

    private static (PeopleController Controller, AerieContext Db, FakeTimeProvider Time) NewController()
    {
        var options = new DbContextOptionsBuilder<AerieContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        var db = new AerieContext(options);
        var time = new FakeTimeProvider(Now);
        var controller = new PeopleController(db, time)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };

        return (controller, db, time);
    }
}
