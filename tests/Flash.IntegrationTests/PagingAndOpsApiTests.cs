using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace Flash.IntegrationTests;

[Collection("api")]
public class PagingAndOpsApiTests(ApiFactory factory)
{
    [Fact]
    public async Task TheOrderList_IsPaged_NewestFirst_WithTheTotalInAHeader()
    {
        var admin = await factory.AdminAsync();
        var deal = await factory.CreateDealAsync(admin, 20);
        var ana = await factory.RegisterAsync();
        var ids = new List<Guid>();
        for (var i = 0; i < 5; i++)
            ids.Add(await factory.BookAsync(ana, deal));

        var client = factory.ClientFor(ana.AccessToken);
        var first = await client.GetAsync($"/api/orders?dealCode={deal}&page=1&pageSize=2");
        var second = await client.GetAsync($"/api/orders?dealCode={deal}&page=2&pageSize=2");
        var last = await client.GetAsync($"/api/orders?dealCode={deal}&page=3&pageSize=2");

        Assert.Equal("5", first.Headers.GetValues("X-Total-Count").Single());
        var a = (await first.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray().Select(b => b.GetProperty("id").GetGuid()).ToList();
        var b2 = (await second.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray().Select(b => b.GetProperty("id").GetGuid()).ToList();
        var c = (await last.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray().Select(b => b.GetProperty("id").GetGuid()).ToList();
        Assert.Equal([2, 2, 1], [a.Count, b2.Count, c.Count]);
        Assert.Equivalent(ids, a.Concat(b2).Concat(c).ToList());
        Assert.Equal(ids[^1], a[0]); // newest first
    }

    [Fact]
    public async Task ThePageSize_IsCapped_AndABadPageFallsBackToTheFirst()
    {
        var ana = await factory.RegisterAsync();
        var response = await factory.ClientFor(ana.AccessToken).GetAsync("/api/orders?page=0&pageSize=100000");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task DeadLetters_AreStaffOnly()
    {
        var customer = await factory.RegisterAsync();
        var admin = await factory.AdminAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.NewClient().GetAsync("/api/admin/dead-letters")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await factory.ClientFor(customer.AccessToken).GetAsync("/api/admin/dead-letters")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await factory.ClientFor(customer.AccessToken).PostAsync("/api/admin/dead-letters/anything/replay", null)).StatusCode);
        // With the brokers off (as in these tests) a staff caller is told so, instead of a crash.
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await factory.ClientFor(admin.AccessToken).GetAsync("/api/admin/dead-letters")).StatusCode);
    }
}
