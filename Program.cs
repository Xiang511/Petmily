using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using PawsPort.Authorization;
using PawsPort.Hubs;
using PawsPort.Middlewares;
using PawsPort.Models;
using PawsPort.Services;
using Scalar.AspNetCore;
using PawsPort.Filters;
using PawsPort.Helpers;
using Microsoft.Extensions.Options;
using Serilog;
using Serilog.Events;
using System.Text;

// 啟動期間的臨時 Logger：讀取設定失敗等早期錯誤也能被記錄（正式 Logger 在下方建立後會取代）
Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateLogger();

// 非 Controller 執行緒上的未處理例外（背景執行緒、Task 等），記為 Fatal
AppDomain.CurrentDomain.UnhandledException += (_, e) =>
    Log.Fatal(e.ExceptionObject as Exception, "偵測到未處理的致命例外，IsTerminating={IsTerminating}", e.IsTerminating);

try
{
    var builder = WebApplication.CreateBuilder(args);
    // 判斷目前是否為本機開發環境
    var isDevelopment = builder.Environment.IsDevelopment();
    // 1. 讀取 JWT 設定
    var jwtSettings = builder.Configuration.GetSection("JwtSettings");
    var secretKey = Encoding.UTF8.GetBytes(jwtSettings["SecretKey"]);

    // 2. 設定驗證服務
    builder.Services.AddAuthentication(options =>
    {
        options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
    })
    .AddJwtBearer(options =>
    {
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                // 先從 Authorization Header 讀取
                var token = context.Request.Headers["Authorization"]
                    .FirstOrDefault()?.Split(" ").Last();

                // 如果 Header 没有，讀 Cookie 
                if (string.IsNullOrEmpty(token))
                {
                    token = context.Request.Cookies["X-Access-Token"];
                }

                context.Token = token;
                return Task.CompletedTask;
            }
        };
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwtSettings["Issuer"],
            ValidAudience = jwtSettings["Audience"],
            IssuerSigningKey = new SymmetricSecurityKey(secretKey),
            ClockSkew = TimeSpan.Zero // 取消預設的 5 分鐘緩衝時間，讓過期更精準
        };
    });

    // 3. 設定授權策略（使用擴展方法）
    builder.Services.AddPawsPortAuthorization();

    // AOP 日誌：全域註冊 ActionLoggingFilter（Controller 進入/離開）
    builder.Services.Configure<RequestLoggingOptions>(
        builder.Configuration.GetSection(RequestLoggingOptions.SectionName));
    builder.Services.AddControllers(options =>
    {
        options.Filters.Add<ActionLoggingFilter>();
    });

    // 4. 設定 CORS 政策
    builder.Services.AddCors(options =>
    {
        options.AddPolicy("PawsPortPolicy", policy =>
        {
            if (isDevelopment)
            {
                //  本機開發環境：允許 Vue 本地 Port
                policy.WithOrigins("https://localhost:5173", "https://localhost:5174", "https://localhost:5175");
            }
            else
            {
                //  線上生產環境：除了本地埠，額外允許你的正式網域與前端串接
                policy.WithOrigins("https://petmily.online", "http://petmily.online");
            }

            policy.AllowAnyHeader()
                  .AllowAnyMethod()
                  .AllowCredentials();
        });
    });

    // Add services to the container.
    builder.Services.AddControllersWithViews();

    // 註冊signalR
    builder.Services.AddSignalR();


    //auth
    builder.Services.AddScoped<AuthService>();
    builder.Services.AddScoped<LoginLogService>();
    builder.Services.AddScoped<GoogleOAuthService>();
    builder.Services.AddScoped<EmailService>();

    // AuthService 需要 HttpContext 讀取目前請求的 JWT 身分
    builder.Services.AddHttpContextAccessor();

    // 註冊 HttpClient
    builder.Services.AddHttpClient();




    // member
    builder.Services.AddScoped<MemberProfileService>();
    builder.Services.AddScoped<MemberPermissionService>();
    builder.Services.AddScoped<MemberBlockListService>();

    // community
    builder.Services.AddScoped<ArticleService>();
    builder.Services.AddScoped<CategoryService>();
    builder.Services.AddScoped<CommentService>();
    builder.Services.AddScoped<FileService>();

    // game
    builder.Services.AddScoped<PlayerService>();
    builder.Services.AddScoped<QuestionsService>();
    builder.Services.AddScoped<ShopService>();

    // pet
    builder.Services.AddScoped<PassPortService>();
    builder.Services.AddScoped<PetService>();
    builder.Services.AddScoped<PetAdoptionService>();
    builder.Services.AddScoped<AdoptionRecordService>();
    builder.Services.AddScoped<MissingReportsService>();
    builder.Services.AddScoped<ClientMissingPetService>();
    builder.Services.AddScoped<PetPassportService>();

    // support
    builder.Services.AddScoped<FaqService>();
    builder.Services.AddScoped<QaService>();
    builder.Services.AddScoped<ENewsletterService>();
    builder.Services.AddScoped<LineBotService>();


    // 註冊資料庫連線
    // 優先順序: User Secrets > 環境變數 > appsettings.json
    string? isLocal = builder.Configuration["IS_LOCAL"];
    string connectionString;

    if (isLocal == "true")
    {
        // 本地開發環境使用 Windows 驗證
        connectionString = "Data Source=.;Initial Catalog=PetDB;Integrated Security=True;Encrypt=False";
    }
    else
    {
        // 生產環境或遠端資料庫
        connectionString = builder.Configuration["PETDB"]
            ?? throw new InvalidOperationException("找不到資料庫連接字串。請設定 User Secrets 或環境變數。");
    }

    builder.Services.AddDbContext<PetDbContext>(options =>
        options.UseSqlServer(connectionString));


    // 設定 Serilog - 完全從 appsettings.json 讀取
    // Service DI


    // 設定 Serilog
    var loggerConfig = new LoggerConfiguration()
        .ReadFrom.Configuration(builder.Configuration)
        .Enrich.FromLogContext();

    // Seq 可以保留在程式碼中動態控制
    string? seqEnabled = builder.Configuration["SEQ_ENABLED"];
    if (seqEnabled?.ToLower() == "true")
    {
        // 網址與 ApiKey 來自設定（Seq:ServerUrl / Seq:ApiKey），ApiKey 請放 User Secrets 或環境變數
        loggerConfig.WriteTo.Seq(
            builder.Configuration["Seq:ServerUrl"] ?? "http://localhost:5341",
            apiKey: builder.Configuration["Seq:ApiKey"]);
    }

    Log.Logger = loggerConfig.CreateLogger();
    builder.Host.UseSerilog();



    builder.Services.AddOpenApi(options =>
    {
        options.AddDocumentTransformer((document, context, cancellationToken) =>
        {
            // 設定 API 基本資訊
            document.Info = new()
            {
                Title = " PawsPort API",
                Version = "v1.0.0",
                Description = """


                    """,
                Contact = new()
                {
                    Name = "PawsPort Development Team",
                    Email = "support@pawsport.com",
                    Url = new Uri("https://github.com/Xiang511/PawsPort")
                },
                License = new()
                {
                    Name = "MIT License",
                    Url = new Uri("https://opensource.org/licenses/MIT")
                }
            };

            // 設定 Tags 順序（依照模組分類）
            var tagOrder = new[]
            {
                "會員管理",
                "權限管理",
                "寵物管理",
                "遊戲系統",
                "社群管理",
                "客服管理"
            };

            // 對現有的 Tags 進行排序
            if (document.Tags != null && document.Tags.Count > 0)
            {
                var sortedTags = document.Tags
                    .OrderBy(tag =>
                    {
                        int index = Array.IndexOf(tagOrder, tag.Name);
                        return index == -1 ? int.MaxValue : index;
                    })
                    .ToList();

                document.Tags.Clear();
                foreach (var tag in sortedTags)
                {
                    document.Tags.Add(tag);
                }
            }

            return Task.CompletedTask;
        });
    });



    var app = builder.Build();


    var loggingOptions = app.Services.GetRequiredService<IOptions<RequestLoggingOptions>>().Value;

    // 順序：CorrelationId -> 請求摘要 -> 例外處理
    // 摘要在例外處理之外層，才能看到最終回應狀態碼（5xx），且不會與例外處理重複記錄堆疊
    app.UseMiddleware<RequestLoggingMiddleware>();

    app.UseSerilogRequestLogging(options =>
    {
        // 等級規則集中在 LogLevelResolver；排除路徑回傳 Verbose 等同不記錄
        options.GetLevel = (httpContext, elapsedMs, ex) =>
            loggingOptions.IsExcluded(httpContext.Request.Path)
                ? LogEventLevel.Verbose
                : LogLevelResolver.Resolve(
                    httpContext.Response.StatusCode,
                    ex,
                    elapsedMs,
                    loggingOptions.SlowRequestThresholdMs);

        options.EnrichDiagnosticContext = (diagnosticContext, httpContext) =>
        {
            diagnosticContext.Set("UserId", httpContext.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value);
            diagnosticContext.Set("Controller", httpContext.Items[ActionLoggingFilter.ControllerItemKey]);
            diagnosticContext.Set("Action", httpContext.Items[ActionLoggingFilter.ActionItemKey]);
        };
    });

    app.UseMiddleware<ExceptionHandlingMiddleware>();

    // Configure the HTTP request pipeline.
    if (!app.Environment.IsDevelopment())
    {
        app.UseExceptionHandler("/Home/Error");
        // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
        app.UseHsts();
    }
    // 啟用 OpenAPI 端點 + Scalar UI（僅開發環境）
    if (app.Environment.IsDevelopment())
    {
        app.MapOpenApi();
        app.MapScalarApiReference(options =>
        {
            options.WithTitle("PawsPort 會員系統 API")
                   .WithDefaultHttpClient(ScalarTarget.JavaScript, ScalarClient.Axios);
        });
    }
    app.UseStaticFiles();

    // 開放讀取 API：讓瀏覽器可以直接透過網址讀取實體圖片
    var articleImagesPath = Path.Combine(
        builder.Environment.WebRootPath,
        "Images",
        "articleimages"
    );
    if (!Directory.Exists(articleImagesPath))
    {
        Directory.CreateDirectory(articleImagesPath);
    }

    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(articleImagesPath),
        RequestPath = "/Images/articleimages"// 圖片的讀取 API 路徑
    });

    // 生產環境由 Cloudflare 處理 HTTPS，不需要在應用層再次重導
    if (isDevelopment)
    {
        app.UseHttpsRedirection();
    }
    app.UseRouting();


    app.UseCors("PawsPortPolicy"); // 啟用 CORS
    app.UseAuthentication(); // 認證：你是誰？
    app.UseAuthorization();  // 授權：你能做什麼？



    app.MapStaticAssets();
    // 根路徑：開發環境導向 Scalar 文件，生產環境導向首頁
    if (app.Environment.IsDevelopment())
    {
        app.MapGet("/", () => Results.Redirect("/scalar/v1")).ExcludeFromDescription();
    }
    else
    {
        app.MapGet("/", () => Results.Redirect("https://petmily.online")).ExcludeFromDescription();
    }

    app.MapControllerRoute(
        name: "default",
        pattern: "{controller=Home}/{action=Index}/{id?}")
        .WithStaticAssets();


    //開啟 AI 機器人的通訊端點
    app.MapHub<AIChatHub>("/aiChatHub");

    app.Run();
}
// EF Core 設計階段工具（migrations）會以 HostAbortedException 中止啟動，屬正常情況不記 Fatal
catch (Exception ex) when (ex is not HostAbortedException)
{
    Log.Fatal(ex, "應用程式啟動失敗或意外終止");
    Environment.ExitCode = 1;
}
finally
{
    // 確保緩衝中的日誌（含 Seq）在結束前送出
    Log.CloseAndFlush();
}
