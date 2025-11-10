using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RemotePlay.Models.Base;
using RemotePlay.Models.Context;
using EnumEntity = RemotePlay.Models.DB.Base.Enum;

namespace RemotePlay.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class EnumController : ControllerBase
    {
        private readonly RPContext _context;
        private readonly ILogger<EnumController> _logger;

        public EnumController(RPContext context, ILogger<EnumController> logger)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// 获取全部可用的系统枚举，可按类型查询。
        /// </summary>
        /// <param name="type">可选，指定要查询的枚举类型。</param>
        /// <param name="cancellationToken">请求取消标记。</param>
        [HttpGet]
        public async Task<ActionResult<ApiSuccessResponse<object>>> GetAllAsync(
            [FromQuery] string? type,
            CancellationToken cancellationToken)
        {
            if (!string.IsNullOrWhiteSpace(type))
            {
                return await GetByTypeInternalAsync(type, cancellationToken);
            }

            var enumList = await _context.Enums
                .AsNoTracking()
                .Where(e => e.IsActive)
                .OrderBy(e => e.EnumType)
                .ThenBy(e => e.SortOrder)
                .ThenBy(e => e.EnumKey)
                .ToListAsync(cancellationToken);

            var grouped = enumList
                .GroupBy(e => e.EnumType, StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    g => g.Key,
                    g => g
                        .Select(MapToDynamic)
                        .Cast<object>()
                        .ToList(),
                    StringComparer.OrdinalIgnoreCase);

            return Ok(new ApiSuccessResponse<object>
            {
                Data = grouped,
                Message = grouped.Count > 0 ? "枚举加载成功" : "未找到枚举数据"
            });
        }

        /// <summary>
        /// 获取某一枚举类型的所有条目。
        /// </summary>
        /// <param name="enumType">枚举类型名称。</param>
        /// <param name="cancellationToken">请求取消标记。</param>
        [HttpGet("{enumType}")]
        public async Task<ActionResult<ApiSuccessResponse<object>>> GetByTypeAsync(
            string enumType,
            CancellationToken cancellationToken)
        {
            return await GetByTypeInternalAsync(enumType, cancellationToken);
        }

        private async Task<ActionResult<ApiSuccessResponse<object>>> GetByTypeInternalAsync(
            string enumType,
            CancellationToken cancellationToken)
        {
            var normalizedType = enumType.Trim();
            if (normalizedType.Length == 0)
            {
                return BadRequest(new ApiErrorResponse
                {
                    ErrorMessage = "枚举类型不能为空。"
                });
            }

            var upperType = normalizedType.ToUpperInvariant();

            var records = await _context.Enums
                .AsNoTracking()
                .Where(e => e.IsActive && e.EnumType.ToUpper() == upperType)
                .OrderBy(e => e.SortOrder)
                .ThenBy(e => e.EnumKey)
                .ToListAsync(cancellationToken);

            if (records.Count == 0)
            {
                _logger.LogWarning("未找到枚举类型 {EnumType}", enumType);
                return NotFound(new ApiErrorResponse
                {
                    ErrorMessage = $"未找到枚举类型: {enumType}"
                });
            }

            var actualType = records[0].EnumType;
            var items = records.Select(MapToDynamic).ToList();

            return Ok(new ApiSuccessResponse<object>
            {
                Data = new
                {
                    EnumType = actualType,
                    Items = items
                },
                Message = $"已加载枚举类型 {actualType}"
            });
        }

        private static object MapToDynamic(EnumEntity entity) => new
        {
            key = entity.EnumKey,
            value = entity.EnumValue,
            code = entity.EnumCode,
            sortOrder = entity.SortOrder,
            description = entity.Description
        };
    }
}

