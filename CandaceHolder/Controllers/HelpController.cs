using Microsoft.AspNetCore.Mvc;
using CandaceHolder.Filters;

namespace CandaceHolder.Controllers
{
    [Route("[controller]")]
    [SkipTrialGate]
    public class HelpController : Controller
    {
        [HttpGet("")]
        public IActionResult Index() => View();
    }
}
 