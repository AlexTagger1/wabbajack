using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Wabbajack.Server.CDN.Controllers
{
    [ApiController]
    [Route("/authored_files")]
    public class CDNFiles : ControllerBase
    {
        private readonly ILogger<CDNFiles> _logger;

        public CDNFiles(ILogger<CDNFiles> logger)
        {
            _logger = logger;
        }

        [HttpPut]
        [Route("{serverAssignedUniqueId}/part/{index}")]
        public async Task<IActionResult> UploadFilePart(string serverAssignedUniqueId, long index)
        {
            var user = User.FindFirstValue(ClaimTypes.Name);
            _logger.Log(LogLevel.Information, $"Uploading File part {serverAssignedUniqueId} - ({index})");
            

            await using var ms = new MemoryStream();
            await Request.Body.CopyToLimitAsync(ms, part.Size);
            ms.Position = 0;
            if (ms.Length != part.Size)
                return BadRequest($"Couldn't read enough data for part {part.Size} vs {ms.Length}");

            var hash = ms.xxHash();
            if (hash != part.Hash)
                return BadRequest($"Hashes don't match for index {index}. Sizes ({ms.Length} vs {part.Size}). Hashes ({hash} vs {part.Hash}");

            ms.Position = 0;
            await UploadAsync(ms, $"{definition.MungedName}/parts/{index}");
            return Ok(part.Hash.ToBase64());
        }
    }
}
