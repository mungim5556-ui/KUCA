using KucaMemoServer.Endpoints;
using KucaMemoServer.Services;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.FileProviders;

// KUCA 장소 메모 서버
// 실행: server/KucaMemoServer 폴더에서 `dotnet run` → 브라우저에서 http://localhost:5080/swagger
var builder = WebApplication.CreateBuilder(args);

// API 문서 화면(Swagger)
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// 캠퍼스 건물 목록 (Data/buildings.json, 앱과 같은 632개)
builder.Services.AddSingleton<BuildingStore>();

// 메모 저장소 (SQLite 파일 memos.db) 와 사진 저장소 (wwwroot/photos)
builder.Services.AddSingleton<MemoStore>();
builder.Services.AddSingleton<PhotoStore>();

// 로그인 · 계정: 카카오 / Apple 로그인 확인, 경희대 메일 인증 (Kakao:AppId, Email:User/Password 설정)
builder.Services.AddSingleton<AccountStore>();
builder.Services.AddHttpClient<IKakaoVerifier, KakaoVerifier>(c => c.Timeout = TimeSpan.FromSeconds(10));
builder.Services.AddHttpClient<IAppleVerifier, AppleVerifier>(c => c.Timeout = TimeSpan.FromSeconds(10));
// 인증 메일: Azure 메일(Email:ConnectionString)이 있으면 그것, 없으면 Gmail SMTP
builder.Services.AddSingleton<AzureEmailSender>();
builder.Services.AddSingleton<SmtpEmailSender>();
builder.Services.AddSingleton<IEmailSender>(sp => sp.GetRequiredService<AzureEmailSender>() is { IsConfigured: true } azure
    ? azure : sp.GetRequiredService<SmtpEmailSender>());

// 쪽지·댓글 내용 검사 (AI: FactChat 게이트웨이, 키가 없거나 장애면 금지어 검사로 대체)
// 설정: Ai:ApiKey (없으면 Recommend:ApiKey 를 같이 씀. 저장소에 넣지 않음), Ai:FilterModel 등
builder.Services.AddHttpClient<IContentModerator, AiModerator>();
// 캠퍼스 날씨 (기상청 초단기 실황·예보, 키는 설정 Kma:ServiceKey)
builder.Services.AddHttpClient();
builder.Services.AddSingleton<KmaWeatherService>();
// 산책 추천: 챗쿠 AI (설정 Recommend:Endpoint / Recommend:ApiKey 가 없으면 규칙 기반으로만 추천)
builder.Services.AddHttpClient<IRouteAi, ChatKuClient>(c => c.Timeout = TimeSpan.FromSeconds(25));
builder.Services.AddScoped<RouteRecommender>();
// 장소 공지 (C-4): 경희스팟 목록, 공지 저장소, AI 공지 판별 (내용 검사와 같은 게이트웨이·키)
builder.Services.AddSingleton<SpotStore>();
builder.Services.AddSingleton<NoticeStore>();
builder.Services.AddHttpClient<INoticeClassifier, AiNoticeClassifier>();

// 메모 작성 본문 크기 제한: 사진 10MB + 글자 여유분
builder.Services.Configure<FormOptions>(o => o.MultipartBodyLengthLimit = PhotoStore.MaxBytes + 1024 * 1024);

var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI();

// wwwroot 폴더의 파일을 그대로 내려준다.
app.UseStaticFiles();

// 업로드된 사진: 사진 폴더를 /photos 로 내려준다. 예: /photos/abc.jpg
// (기본은 wwwroot/photos 라 위에서도 내려가지만, Photos:Directory 로 앱 밖에 둔 경우를 위해 따로 연결한다)
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(app.Services.GetRequiredService<PhotoStore>().DirectoryPath),
    RequestPath = "/photos",
});

app.MapGet("/api/health", () => Results.Ok(new { status = "ok" }))
   .WithTags("Health");

app.MapBuildingEndpoints();
app.MapMemoEndpoints();
app.MapWeatherEndpoints();
app.MapReactionEndpoints();
app.MapRecommendEndpoints();
app.MapNoticeEndpoints();
app.MapAuthEndpoints();
app.MapAdminEndpoints();
app.MapWebPages();

app.Run();

// 테스트(WebApplicationFactory)에서 이 서버를 띄울 수 있게 공개한다.
public partial class Program;
