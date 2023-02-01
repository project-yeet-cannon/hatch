using Aerie.Api.Controllers;

namespace Aerie.Api.Tests.Controllers
{
    public class HelloWorldControllerTests
    {
        [Fact]
        public void HelloWorld_ShouldSayHello()
        {
            var sut = new HelloWorldController(
                new NullLogger<HelloWorldController>());

            var result = sut.HelloWorld();

            Assert.Equal("Hello, world!", result);
        }
    }
}
