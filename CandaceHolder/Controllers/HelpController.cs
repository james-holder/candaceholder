using Microsoft.AspNetCore.Mvc;

namespace CandaceHolder.Controllers
{
    [Route("[controller]")]
    public class HelpController : Controller
    {
        [HttpGet("")]
        public IActionResult Index() => View();
    }
}
 