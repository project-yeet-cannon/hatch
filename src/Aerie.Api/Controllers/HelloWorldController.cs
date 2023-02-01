using Microsoft.AspNetCore.Mvc;

namespace Aerie.Api.Controllers;

[ApiController]
[Route("[controller]")]
public class HelloWorldController : ControllerBase
{
    private readonly ILogger _logger;

    public HelloWorldController(ILogger<HelloWorldController> logger)
    {
        _logger = logger;
    }

    [HttpGet("")]
    public string HelloWorld()
    {
        return "Hello, world!";
    }
}
