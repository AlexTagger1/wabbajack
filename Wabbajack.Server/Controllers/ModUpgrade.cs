﻿using System;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Wabbajack.Common;
using Wabbajack.Lib;
using Wabbajack.Server.DataLayer;
using Wabbajack.Server.Services;

namespace Wabbajack.BuildServer.Controllers
{
    [ApiController]
    public class ModUpgrade : ControllerBase
    {
        private ILogger<ModUpgrade> _logger;
        private SqlService _sql;
        private DiscordWebHook _discord;
        private AppSettings _settings;
        private QuickSync _quickSync;

        public ModUpgrade(ILogger<ModUpgrade> logger, SqlService sql, DiscordWebHook discord, QuickSync quickSync, AppSettings settings)
        {
            _logger = logger;
            _sql = sql;
            _discord = discord;
            _settings = settings;
            _quickSync = quickSync;
        }
        
        [HttpPost]
        [Authorize(Roles = "User")]
        [Route("/mod_upgrade")]
        public async Task<IActionResult> PostModUpgrade()
        {
            var isAuthor = User.Claims.Any(c => c.Type == ClaimTypes.Role && c.Value == "Author");
            var request = (await Request.Body.ReadAllTextAsync()).FromJsonString<ModUpgradeRequest>();
            if (!isAuthor)
            {
                var srcDownload = await _sql.GetArchiveDownload(request.OldArchive.State.PrimaryKeyString,
                    request.OldArchive.Hash, request.OldArchive.Size);
                var destDownload = await _sql.GetArchiveDownload(request.NewArchive.State.PrimaryKeyString,
                    request.NewArchive.Hash, request.NewArchive.Size);

                if (srcDownload == default || destDownload == default ||
                    await _sql.FindPatch(srcDownload.Id, destDownload.Id) == default)
                {
                    if (!await request.IsValid())
                    {
                        _logger.Log(LogLevel.Information,
                            $"Upgrade requested from {request.OldArchive.Hash} to {request.NewArchive.Hash} rejected as upgrade is invalid");
                        return BadRequest("Invalid mod upgrade");
                    }

                    if (_settings.ValidateModUpgrades && !await _sql.HashIsInAModlist(request.OldArchive.Hash))
                    {
                        _logger.Log(LogLevel.Information,
                            $"Upgrade requested from {request.OldArchive.Hash} to {request.NewArchive.Hash} rejected as src hash is not in a curated modlist");
                        return BadRequest("Hash is not in a recent modlist");
                    }

                }

            }

            try
            {
                if (await request.OldArchive.State.Verify(request.OldArchive))
                {
                    _logger.LogInformation(
                        $"Refusing to upgrade ({request.OldArchive.State.PrimaryKeyString}), old archive is valid");
                    return NotFound("File is Valid");
                }
            }
            catch (Exception ex)
            {
                _logger.LogInformation(
                    $"Refusing to upgrade ({request.OldArchive.State.PrimaryKeyString}), due to upgrade failure");
                return NotFound("File is Valid");
            }

            var oldDownload = await _sql.GetOrEnqueueArchive(request.OldArchive);
            var newDownload = await _sql.GetOrEnqueueArchive(request.NewArchive);

            var patch = await _sql.FindOrEnqueuePatch(oldDownload.Id, newDownload.Id);
            if (patch.Finished.HasValue)
            {
                if (patch.PatchSize != 0)
                {
                    _logger.Log(LogLevel.Information, $"Upgrade requested from {oldDownload.Hash} to {newDownload.Hash} patch Found");
                    await _sql.MarkPatchUsage(oldDownload.Id, newDownload.Id);
                    return
                        Ok(
                            $"https://{_settings.BunnyCDN_StorageZone}.b-cdn.net/{Consts.ArchiveUpdatesCDNFolder}/{request.OldArchive.Hash.ToHex()}_{request.NewArchive.Hash.ToHex()}");
                }
                _logger.Log(LogLevel.Information, $"Upgrade requested from {oldDownload.Hash} to {newDownload.Hash} patch found but was failed");

                return NotFound("Patch creation failed");
            }

            if (!newDownload.DownloadFinished.HasValue)
            {
                await _quickSync.Notify<ArchiveDownloader>();
            }
            else
            {
                await _quickSync.Notify<PatchBuilder>();
            }
            
            _logger.Log(LogLevel.Information, $"Upgrade requested from {oldDownload.Hash} to {newDownload.Hash} patch found is processing");
            // Still processing
            return Accepted();
        }

        [HttpGet]
        [Authorize(Roles = "User")]
        [Route("/mod_upgrade/find/{hashAsHex}")]
        public async Task<IActionResult> FindUpgrade(string hashAsHex)
        {
            var hash = Hash.FromHex(hashAsHex);

            var patches = await _sql.PatchesForSource(hash);
            return Ok(patches.Select(p => p.Dest.ToArchive()).ToList().ToJson());
        }
      

    }
}
