using System.Net;
using System.Net.Http;
using System.Text;
using TraeTool.Services;
using Xunit;

namespace TraeTool.Tests;

public class ModelCatalogTests
{
    /// <summary>按实际观测到的模型配置响应构造（截取样例，含需被过滤的功能开关项）。</summary>
    private const string ObservedConfig = """
        {"allow_tenant_user_add_model":true,
         "function_configs":[{"config_info_list":[
           {"config_name":"Doubao-Seed-2.1-Turbo","config_switch":true,
            "display_config":{"display_name":"Seed-2.1-Turbo","model_capability":"reasoning_model"}},
           {"config_name":"DeepSeek-V4-Flash","config_switch":true,
            "display_config":{"display_name":"DeepSeek-V4-Flash","model_capability":"reasoning_model"}},
           {"config_name":"kimi-k2","config_switch":true,
            "display_config":{"display_name":"Kimi-K2-0905","model_capability":"chat_model"}},
           {"config_name":"custom_claude-sonnet-4","config_switch":true,
            "display_config":{"display_name":"Claude-4-Sonnet","model_capability":"chat_model"}},
           {"config_name":"fast_apply","config_switch":true,"display_config":{"display_name":""}},
           {"config_name":"summary","config_switch":true,"display_config":{"display_name":""}},
           {"config_name":"disabled-model","config_switch":false,
            "display_config":{"display_name":"Disabled"}}
         ]}]}
        """;

    [Fact]
    public void ParseModelIds_reads_config_names_with_display()
    {
        var ids = ModelCatalog.ParseModelIds(ObservedConfig);

        Assert.Equal(new[] { "Doubao-Seed-2.1-Turbo", "DeepSeek-V4-Flash", "kimi-k2", "custom_claude-sonnet-4" }, ids);
    }

    [Fact]
    public void ParseModelIds_reads_top_level_config_info_list()
    {
        var ids = ModelCatalog.ParseModelIds("""
            {"config_info_list":[{"config_name":"deepsearch","config_switch":true,
              "display_config":{"display_name":"deepsearch","model_capability":"chat_model"}}]}
            """);

        Assert.Equal(new[] { "deepsearch" }, ids);
    }

    [Fact]
    public void ParseModelIds_falls_back_to_model_configs_keys()
    {
        var ids = ModelCatalog.ParseModelIds(
            """{"model_configs":{"CodeDS":"x","CodeZ":"x","S-AB-1":"x","S-AB-2":"x","S-CodeZ":"x"}}""");

        Assert.Equal(new[] { "CodeDS", "CodeZ", "S-AB-1", "S-AB-2", "S-CodeZ" }, ids);
    }

    [Fact]
    public void ParseModelIds_supports_models_array_shape()
    {
        var ids = ModelCatalog.ParseModelIds("""{"models":[{"id":"alpha"},{"id":"beta"}]}""");

        Assert.Equal(new[] { "alpha", "beta" }, ids);
    }

    [Fact]
    public void ParseModelIds_invalid_json_returns_empty()
    {
        Assert.Empty(ModelCatalog.ParseModelIds("<html>502</html>"));
    }

    [Theory]
    [InlineData("trae[1M]", "DeepSeek-V4-Flash")]   // 别名 + Claude 系客户端的上下文标记
    [InlineData("trae", "DeepSeek-V4-Flash")]
    [InlineData("TRE[200k]", "DeepSeek-V4-Flash")]
    [InlineData("", "DeepSeek-V4-Flash")]
    [InlineData("gpt-4o", "DeepSeek-V4-Flash")]      // 未知名字兜底到默认模型
    [InlineData("DeepSeek-V4-Flash", "DeepSeek-V4-Flash")]
    [InlineData("deepseek-v4-flash", "DeepSeek-V4-Flash")] // 大小写按目录对齐
    [InlineData("kimi-k2", "kimi-k2")]
    [InlineData("custom_claude-sonnet-4", "custom_claude-sonnet-4")]
    public void Resolve_maps_requested_to_config_name(string requested, string expected)
    {
        var ids = ModelCatalog.ParseModelIds(ObservedConfig);

        Assert.Equal(expected, ModelCatalog.Resolve(requested, ids));
    }

    [Fact]
    public void Resolve_falls_back_to_known_ids_when_list_empty()
    {
        Assert.Equal(ModelCatalog.KnownIds[0], ModelCatalog.Resolve("trae[1M]", Array.Empty<string>()));
    }

    [Fact]
    public void Resolve_uses_first_entry_when_default_absent_from_catalog()
    {
        Assert.Equal("some-model", ModelCatalog.Resolve("trae", new[] { "some-model", "other-model" }));
    }

    [Fact]
    public void KnownIds_starts_with_preferred_model()
    {
        Assert.Equal(ModelCatalog.PreferredModel, ModelCatalog.KnownIds[0]);
    }

    [Fact]
    public void ParseManual_splits_comma_newline_and_separator()
    {
        var list = ModelCatalog.ParseManual("a, b\r\nc、d;d");

        Assert.Equal(new[] { "a", "b", "c", "d" }, list);
    }

    [Fact]
    public void ParseManual_empty_returns_empty()
    {
        Assert.Empty(ModelCatalog.ParseManual(null));
        Assert.Empty(ModelCatalog.ParseManual("  \n  "));
    }

    [Fact]
    public void Normalize_strips_context_marker_prefix_and_separators()
    {
        Assert.Equal("trae", ModelCatalog.Normalize("trae[1M]"));
        Assert.Equal("kimik2", ModelCatalog.Normalize("Kimi-K2"));
        Assert.Equal("kimik2", ModelCatalog.Normalize("openai/Kimi-K2"));
        Assert.Equal("kimik2", ModelCatalog.Normalize("models/kimi-k2"));
    }

    [Fact]
    public void Resolve_infers_by_normalized_inclusion()
    {
        var ids = ModelCatalog.ParseModelIds(ObservedConfig);

        Assert.Equal("kimi-k2", ModelCatalog.Resolve("openai/Kimi-K2", ids));
        Assert.Equal("custom_claude-sonnet-4", ModelCatalog.Resolve("claude-sonnet-4", ids));
    }

    [Fact]
    public void RecordRequested_keeps_canonical_name_and_dedupes()
    {
        var cfg = new TraeTool.Models.AppSettings();

        Assert.True(ModelCatalog.RecordRequested(cfg, "openai/Kimi-K2[1M]"));  // 去掉前缀与标记
        Assert.False(ModelCatalog.RecordRequested(cfg, "Kimi-K2"));             // 大小写不敏感去重
        Assert.False(ModelCatalog.RecordRequested(cfg, "kimi-k2"));
        Assert.False(ModelCatalog.RecordRequested(cfg, "trae[1M]"));            // 别名不记

        // 存的是原样名（保留分隔符），归一化名不能进列表——否则会顶替真实模型 id
        Assert.Contains("Kimi-K2", cfg.GatewaySeenModels);
        Assert.DoesNotContain("openai/", cfg.GatewaySeenModels);
        Assert.DoesNotContain("[1M]", cfg.GatewaySeenModels);
    }

    [Fact]
    public void RecordRequested_caps_seen_list_and_drops_oldest()
    {
        var cfg = new TraeTool.Models.AppSettings();
        var total = ModelCatalog.MaxSeenModels + 5;
        for (int i = 0; i < total; i++)
            Assert.True(ModelCatalog.RecordRequested(cfg, "model-" + i));

        var seen = ModelCatalog.ParseSeen(cfg.GatewaySeenModels);
        Assert.Equal(ModelCatalog.MaxSeenModels, seen.Count);
        Assert.DoesNotContain("model-0", seen);              // 最早的被挤掉
        Assert.Contains("model-" + (total - 1), seen);       // 最新的保留
    }

    [Fact]
    public void RecordRequested_ignores_overlong_names()
    {
        var cfg = new TraeTool.Models.AppSettings();

        Assert.False(ModelCatalog.RecordRequested(cfg, new string('x', 65)));
        Assert.Null(cfg.GatewaySeenModels);
    }

    [Fact]
    public void ParseSeen_empty_returns_empty()
    {
        Assert.Empty(ModelCatalog.ParseSeen(null));
        Assert.Empty(ModelCatalog.ParseSeen("  \n  "));
    }

    [Fact]
    public async Task AggregateAsync_puts_manual_first_and_dedupes_across_layers()
    {
        var cfg = new TraeTool.Models.AppSettings
        {
            ModelOverrides = new List<TraeTool.Models.ModelOverride>
            {
                new() { Id = "my-model" },
                new() { Id = "kimi-k2" },
            },
            GatewaySeenModels = "seen-model",
        };
        ModelCatalog.Reset();
        try
        {
            var account = new TraeTool.Models.Account { Token = "token" };
            var (ids, sources) = await ModelCatalog.AggregateAsync(cfg, account,
                new HttpClient(new StubModelHandler()));

            Assert.Equal("my-model", ids[0]);                       // 人工维护优先
            Assert.Equal(2, sources.Manual);
            Assert.Equal(4, sources.Official);                      // 官网同步层来自假上游
            Assert.True(sources.Live);
            Assert.Contains("kimi-k2", ids);
            Assert.Contains(ModelCatalog.KnownIds[0], ids);         // 内置层
            Assert.Contains("seen-model", ids);                     // 推断层
            Assert.Equal(ids.Count, ids.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        }
        finally
        {
            ModelCatalog.Reset();
        }
    }

    // ==================== 覆盖项（模型列表的增删改） ====================

    private static TraeTool.Models.ModelOverride Over(string id,
        string? display = null, string? vendor = null, bool hidden = false)
        => new() { Id = id, Display = display, Vendor = vendor, Hidden = hidden };

    [Fact]
    public void Override_fields_win_and_missing_fields_fall_back_to_the_builtin_layer()
    {
        var cfg = new TraeTool.Models.AppSettings
        {
            ModelOverrides = new List<TraeTool.Models.ModelOverride> { Over("glm-5.3", display: "我的 GLM") },
        };
        ModelCatalog.Reset();
        try
        {
            var (entries, _) = ModelCatalog.MergeCached(cfg);
            var entry = entries.First(e => e.Id == "glm-5.3");

            Assert.Equal("我的 GLM", entry.Display);          // 用户填的盖住官网/内置
            Assert.Equal("智谱", entry.Vendor);              // 没填的由内置层补齐
            Assert.Equal("custom", entry.Source);
        }
        finally
        {
            ModelCatalog.Reset();
        }
    }

    [Fact]
    public void Override_can_add_a_model_that_no_layer_knows()
    {
        var cfg = new TraeTool.Models.AppSettings
        {
            ModelOverrides = new List<TraeTool.Models.ModelOverride>
            {
                Over("my-local-model", display: "本地模型", vendor: "自建"),
            },
        };
        ModelCatalog.Reset();
        try
        {
            var (entries, sources) = ModelCatalog.MergeCached(cfg);
            var entry = entries.First(e => e.Id == "my-local-model");

            Assert.Equal("本地模型", entry.DisplayText);
            Assert.Equal("自建", entry.VendorText);
            Assert.Equal(1, sources.Manual);
        }
        finally
        {
            ModelCatalog.Reset();
        }
    }

    [Fact]
    public void Hidden_override_survives_every_layer()
    {
        // kimi-k2 在官网层与内置层都有：隐藏后必须从两层都消失
        var cfg = new TraeTool.Models.AppSettings
        {
            ModelOverrides = new List<TraeTool.Models.ModelOverride> { Over("kimi-k2", hidden: true) },
        };
        ModelCatalog.Reset();
        try
        {
            var (entries, sources) = ModelCatalog.MergeCached(cfg);
            Assert.DoesNotContain(entries, e => e.Id == "kimi-k2");
            Assert.Equal(1, sources.Hidden);
            Assert.True(ModelCatalog.IsHidden(cfg, "kimi-k2"));
            Assert.True(ModelCatalog.IsHidden(cfg, "KIMI-K2"));   // 大小写无关

            // 「显示已隐藏」时恢复成一行，供界面恢复
            var (withHidden, _) = ModelCatalog.MergeCached(cfg, includeHidden: true);
            var row = Assert.Single(withHidden, e => e.Id == "kimi-k2");
            Assert.Equal("custom", row.Source);
        }
        finally
        {
            ModelCatalog.Reset();
        }
    }

    [Fact]
    public void Hidden_matches_variants_of_the_same_name()
    {
        // 客户端可能按带上下文标记的名字请求：隐藏要连变体一起挡掉
        var cfg = new TraeTool.Models.AppSettings
        {
            ModelOverrides = new List<TraeTool.Models.ModelOverride> { Over("deepseek-v4.1-flash", hidden: true) },
            GatewaySeenModels = "DeepSeek-V4.1-Flash[1M]",
        };
        ModelCatalog.Reset();
        try
        {
            var (entries, _) = ModelCatalog.MergeCached(cfg);
            Assert.DoesNotContain(entries, e => ModelCatalog.Normalize(e.Id) == "deepseekv41flash");
        }
        finally
        {
            ModelCatalog.Reset();
        }
    }

    [Fact]
    public void Override_with_a_blank_id_is_ignored()
    {
        var cfg = new TraeTool.Models.AppSettings
        {
            ModelOverrides = new List<TraeTool.Models.ModelOverride> { Over("   "), Over("") },
        };
        ModelCatalog.Reset();
        try
        {
            var (entries, sources) = ModelCatalog.MergeCached(cfg);
            Assert.Equal(0, sources.Manual);
            Assert.DoesNotContain(entries, e => string.IsNullOrWhiteSpace(e.Id));
        }
        finally
        {
            ModelCatalog.Reset();
        }
    }

    [Fact]
    public void Duplicate_overrides_of_one_name_are_collapsed()
    {
        var cfg = new TraeTool.Models.AppSettings
        {
            ModelOverrides = new List<TraeTool.Models.ModelOverride>
            {
                Over("glm-5.3", display: "第一"),
                Over("GLM-5.3", display: "第二"),
            },
        };
        ModelCatalog.Reset();
        try
        {
            var (entries, _) = ModelCatalog.MergeCached(cfg);
            Assert.Single(entries, e => ModelCatalog.Normalize(e.Id) == "glm53");
            Assert.Equal("第一", entries.First(e => ModelCatalog.Normalize(e.Id) == "glm53").Display);
        }
        finally
        {
            ModelCatalog.Reset();
        }
    }

    /// <summary>返回固定模型配置响应的假上游。</summary>
    private sealed class StubModelHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(ObservedConfig, Encoding.UTF8, "application/json"),
            });
    }
}
