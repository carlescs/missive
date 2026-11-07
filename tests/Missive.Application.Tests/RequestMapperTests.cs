using Missive.Application.Requests;

namespace Missive.Application.Tests;

public class RequestMapperTests
{
    [Fact]
    public void Description_RoundTripsWithLfLineEndings()
    {
        var dto = RequestDtoFactory.Create() with { Description = "# Notes\r\n\r\nUse *staging*.\r\n" };

        var domain = RequestMapper.ToDomain(dto);

        Assert.True(domain.IsSuccess, domain.Error?.Message);
        Assert.Equal("# Notes\n\nUse *staging*.\n", domain.Value.Description);
        Assert.Equal(domain.Value.Description, RequestMapper.ToDto(domain.Value).Description);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  \n\t\n")]
    public void Description_BlankIsNone(string? description)
    {
        var domain = RequestMapper.ToDomain(RequestDtoFactory.Create() with { Description = description });

        Assert.Equal(string.Empty, domain.Value.Description);
    }
}
