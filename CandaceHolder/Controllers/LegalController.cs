using Microsoft.AspNetCore.Mvc;
using CandaceHolder.Filters;

namespace CandaceHolder.Controllers
{
    [Route("[controller]")]
    [SkipTrialGate]
    public class LegalController : Controller
    {
        [HttpGet("privacy")]
        public IActionResult Privacy() => View();

        [HttpGet("terms")]
        public IActionResult Terms() => View();
    }
}
