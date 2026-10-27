using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Flash.Orders;
using Flash.Receipts;
using Xunit;

namespace Flash.IntegrationTests;

[Collection("api")]
public class AuthorizationApiTests(ApiFactory factory)
{
    [Theory]
    [InlineData("/api/deals")]
    [InlineData("/api/orders")]
    [InlineData("/api/receipts/11111111-1111-1111-1111-111111111111")]
    [InlineData("/api/orders/11111111-1111-1111-1111-111111111111/events")]
    public async Task EverythingButHealthAndAuth_RequiresASignedInUser(string path) =>
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.NewClient().GetAsync(path)).StatusCode);

    [Fact]
    public async Task WritesRequireASignedInUser()
    {
        var client = factory.NewClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/deals", new { code = "X" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/orders", new { dealCode = "X" })).StatusCode);
    }

    [Fact]
    public async Task Health_IsOpen_AndReadinessChecksTheDatabases()
    {
        var client = factory.NewClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/health")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/health/ready")).StatusCode);
    }

    [Fact]
    public async Task ACustomer_CannotOpenADeal_ButAnAdministratorCan()
    {
        var customer = await factory.RegisterAsync();
        var admin = await factory.AdminAsync();
        var body = ApiHelpers.DealBody(ApiHelpers.NewCode(), 10);

        Assert.Equal(HttpStatusCode.Forbidden, (await factory.ClientFor(customer.AccessToken).PostAsJsonAsync("/api/deals", body)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await factory.ClientFor(admin.AccessToken).PostAsJsonAsync("/api/deals", body)).StatusCode);
    }

    [Fact]
    public async Task ACustomer_CanReadTheCatalog()
    {
        var admin = await factory.AdminAsync();
        var code = await factory.CreateDealAsync(admin);
        var customer = await factory.RegisterAsync();

        var deal = await factory.ClientFor(customer.AccessToken).GetAsync($"/api/deals/{code}");
        Assert.Equal(HttpStatusCode.OK, deal.StatusCode);
    }

    [Fact]
    public async Task AOrder_BelongsToItsOwner()
    {
        var admin = await factory.AdminAsync();
        var deal = await factory.CreateDealAsync(admin);
        var ana = await factory.RegisterAsync();
        var bruno = await factory.RegisterAsync();
        var id = await factory.BookAsync(ana, deal);

        Assert.Equal(HttpStatusCode.OK, (await factory.ClientFor(ana.AccessToken).GetAsync($"/api/orders/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await factory.ClientFor(admin.AccessToken).GetAsync($"/api/orders/{id}")).StatusCode);
        // Another customer learns nothing, not even that the id exists.
        Assert.Equal(HttpStatusCode.NotFound, (await factory.ClientFor(bruno.AccessToken).GetAsync($"/api/orders/{id}")).StatusCode);
    }

    [Fact]
    public async Task AnotherCustomer_CannotRefundSomeoneElsesOrder()
    {
        var admin = await factory.AdminAsync();
        var deal = await factory.CreateDealAsync(admin);
        var ana = await factory.RegisterAsync();
        var bruno = await factory.RegisterAsync();
        var id = await factory.BookAsync(ana, deal);

        var attempt = await factory.ClientFor(bruno.AccessToken).PostAsync($"/api/orders/{id}/refund", null);
        Assert.Equal(HttpStatusCode.NotFound, attempt.StatusCode);
    }

    [Fact]
    public async Task TheList_ShowsACustomerOnlyTheirOwnOrders_AndStaffEverything()
    {
        var admin = await factory.AdminAsync();
        var deal = await factory.CreateDealAsync(admin);
        var ana = await factory.RegisterAsync();
        var bruno = await factory.RegisterAsync();
        var anas = await factory.BookAsync(ana, deal);
        var brunos = await factory.BookAsync(bruno, deal);

        var seenByAna = await factory.ClientFor(ana.AccessToken).GetFromJsonAsync<JsonElement>($"/api/orders?dealCode={deal}");
        var seenByStaff = await factory.ClientFor(admin.AccessToken).GetFromJsonAsync<JsonElement>($"/api/orders?dealCode={deal}");

        Assert.Equal([anas], seenByAna.EnumerateArray().Select(b => b.GetProperty("id").GetGuid()).ToArray());
        Assert.Equivalent(new[] { anas, brunos }, seenByStaff.EnumerateArray().Select(b => b.GetProperty("id").GetGuid()).ToArray());
    }

    [Fact]
    public async Task TheOrderOwnerIsTheSignedInUser_NotWhatTheBodySays()
    {
        var admin = await factory.AdminAsync();
        var deal = await factory.CreateDealAsync(admin);
        var ana = await factory.RegisterAsync();

        var response = await factory.ClientFor(ana.AccessToken).PostAsJsonAsync("/api/orders",
            new { dealCode = deal, reference = "SHP-OWNER-1", pieces = 1, ownerId = Guid.NewGuid() });

        var order = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(ana.UserId, order.GetProperty("ownerId").GetGuid());
    }

    [Fact]
    public async Task TheEventLog_IsForStaffOnly()
    {
        var admin = await factory.AdminAsync();
        var deal = await factory.CreateDealAsync(admin);
        var ana = await factory.RegisterAsync();
        var id = await factory.BookAsync(ana, deal);

        Assert.Equal(HttpStatusCode.Forbidden, (await factory.ClientFor(ana.AccessToken).GetAsync($"/api/orders/{id}/events")).StatusCode);
    }

    [Fact]
    public async Task AReceipt_IsVisibleToItsOwnerAndStaffOnly()
    {
        var admin = await factory.AdminAsync();
        var ana = await factory.RegisterAsync();
        var bruno = await factory.RegisterAsync();
        var orderId = Guid.CreateVersion7();
        await using (var db = TestDatabase.Receipts())
        {
            db.Receipts.Add(new Receipt { OrderId = orderId, OwnerId = ana.UserId, Number = "FL-X", Status = ReceiptStatus.Issued, CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }

        Assert.Equal(HttpStatusCode.OK, (await factory.ClientFor(ana.AccessToken).GetAsync($"/api/receipts/{orderId}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await factory.ClientFor(admin.AccessToken).GetAsync($"/api/receipts/{orderId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await factory.ClientFor(bruno.AccessToken).GetAsync($"/api/receipts/{orderId}")).StatusCode);
    }

    [Fact]
    public async Task Validation_StillAppliesToSignedInUsers()
    {
        var admin = await factory.AdminAsync();
        var deal = await factory.CreateDealAsync(admin);
        var ana = await factory.RegisterAsync();
        var client = factory.ClientFor(ana.AccessToken);

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/orders", new { dealCode = deal, reference = "", pieces = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/orders", new { dealCode = deal, reference = "COL", pieces = OrderRules.MaxPiecesPerOrder + 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync("/api/orders", new { dealCode = "NOPE", reference = "COL", pieces = 1 })).StatusCode);
    }

    [Fact]
    public async Task EveryResponse_CarriesSecurityHeaders_AndEchoesTheCorrelationId()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/deals");
        request.Headers.Add("X-Correlation-Id", "trace-abc-123");

        var response = await factory.NewClient().SendAsync(request);

        Assert.Equal("trace-abc-123", response.Headers.GetValues("X-Correlation-Id").Single());
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
        Assert.Contains("default-src 'none'", response.Headers.GetValues("Content-Security-Policy").Single());
    }

    [Fact]
    public async Task ProblemResponses_AreProblemJson_WithTheCorrelationId()
    {
        var admin = await factory.AdminAsync();
        var response = await factory.ClientFor(admin.AccessToken).PostAsJsonAsync("/api/deals", new { code = "" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(problem.TryGetProperty("correlationId", out _));
    }
}
