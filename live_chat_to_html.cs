// live_chat_to_html.cs
//
// yt-dlp로 받은 *.live_chat.json 파일을
//   - 읽기 쉬운 텍스트 로그 (.txt)
//   - 프로필 사진/이모지 이미지가 표시되는 HTML 채팅 뷰어 (.html)
// 로 변환합니다.
//
// 실행 (.NET 10 SDK, file-based apps):
//   dotnet run live_chat_to_html.cs -- "제목.live_chat.json"
//
// 출력:
//   "제목.live_chat.txt"
//   "제목.live_chat.html"
//
// 참고: HTML 뷰어는 작성자 프로필 사진(yt3/yt4.ggpht.com)과 이모지 이미지를
// YouTube CDN에서 직접 불러오므로, 인터넷에 연결된 상태에서 열어야
// 이미지가 정상적으로 표시됩니다.

using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

if (args.Length < 1)
{
    Console.WriteLine("사용법: dotnet run live_chat_to_html.cs -- <파일1.live_chat.json> [파일2.live_chat.json ...]");
    Console.WriteLine("        dotnet run live_chat_to_html.cs -- *.live_chat.json");
    return;
}

// PowerShell/cmd 등에서는 "*.live_chat.json" 같은 와일드카드가 셸에서 확장되지 않고
// 그대로 문자열로 전달되는 경우가 있어, 여기서 직접 확장한다.
var inputPaths = new List<string>();
foreach (var arg in args)
{
    if (arg.Contains('*') || arg.Contains('?'))
    {
        string dirPart = Path.GetDirectoryName(arg) is { Length: > 0 } d ? d : ".";
        string pattern = Path.GetFileName(arg);

        if (!Directory.Exists(dirPart))
        {
            Console.WriteLine($"[건너뜀] 디렉터리를 찾을 수 없습니다: {dirPart}");
            continue;
        }

        var matches = Directory.GetFiles(dirPart, pattern);
        if (matches.Length == 0)
        {
            Console.WriteLine($"[건너뜀] 패턴과 일치하는 파일이 없습니다: {arg}");
            continue;
        }

        inputPaths.AddRange(matches);
    }
    else
    {
        inputPaths.Add(arg);
    }
}

foreach (var inPath in inputPaths)
{
    if (!File.Exists(inPath))
    {
        Console.WriteLine($"[건너뜀] 파일을 찾을 수 없습니다: {inPath}");
        continue;
    }

    string stem = Path.GetFileName(inPath);
    foreach (var suffix in new[] { ".live_chat.json", ".json" })
    {
        if (stem.EndsWith(suffix, StringComparison.Ordinal))
        {
            stem = stem[..^suffix.Length];
            break;
        }
    }

    Console.WriteLine($"== {inPath} ==");

    var entries = ParseEntries(inPath);
    Console.WriteLine($"총 {entries.Count}개 메시지 파싱 완료");

    string dir = Path.GetDirectoryName(Path.GetFullPath(inPath))!;
    string txtOut = Path.Combine(dir, $"{stem}.live_chat.txt");
    string htmlOut = Path.Combine(dir, $"{stem}.live_chat.html");

    WriteTextLog(entries, txtOut);
    WriteHtml(entries, htmlOut, stem);

    Console.WriteLine($"텍스트 로그: {txtOut}");
    Console.WriteLine($"HTML 뷰어:  {htmlOut}");
    Console.WriteLine();
}

// ----- 이하 로컬 함수 -----

static List<ChatEntry> ParseEntries(string path)
{
    var raw = new List<RawEntry>();

    foreach (var rawLine in File.ReadLines(path))
    {
        string line = rawLine.Trim();
        if (line.Length == 0)
        {
            continue;
        }

        JsonNode? data;
        try
        {
            data = JsonNode.Parse(line);
        }
        catch (JsonException)
        {
            continue;
        }

        var replay = data?["replayChatItemAction"];
        long? offsetMs = null;
        var offsetNode = replay?["videoOffsetTimeMsec"];
        if (offsetNode != null && long.TryParse(offsetNode.ToString(), out var parsedOffset))
        {
            offsetMs = parsedOffset;
        }

        var actions = replay?["actions"]?.AsArray();
        if (actions == null)
        {
            continue;
        }

        foreach (var action in actions)
        {
            var item = action?["addChatItemAction"]?["item"];
            if (item == null)
            {
                continue;
            }

            if (item["liveChatTextMessageRenderer"] is JsonNode textRenderer)
            {
                raw.Add(new RawEntry(
                    Type: "text",
                    OffsetMs: offsetMs,
                    TimestampUsec: GetTimestampUsec(textRenderer),
                    Author: GetAuthor(textRenderer),
                    AuthorPhotoUrl: GetAuthorPhotoUrl(textRenderer),
                    MessageParts: ExtractMessageParts(textRenderer["message"]),
                    Amount: null,
                    StickerUrl: null
                ));
            }
            else if (item["liveChatPaidMessageRenderer"] is JsonNode paidRenderer)
            {
                raw.Add(new RawEntry(
                    Type: "superchat",
                    OffsetMs: offsetMs,
                    TimestampUsec: GetTimestampUsec(paidRenderer),
                    Author: GetAuthor(paidRenderer),
                    AuthorPhotoUrl: GetAuthorPhotoUrl(paidRenderer),
                    MessageParts: ExtractMessageParts(paidRenderer["message"]),
                    Amount: paidRenderer["purchaseAmountText"]?["simpleText"]?.GetValue<string>() ?? "",
                    StickerUrl: null
                ));
            }
            else if (item["liveChatMembershipItemRenderer"] is JsonNode membershipRenderer)
            {
                var header = membershipRenderer["headerSubtext"] ?? membershipRenderer["headerPrimaryText"];
                raw.Add(new RawEntry(
                    Type: "membership",
                    OffsetMs: offsetMs,
                    TimestampUsec: GetTimestampUsec(membershipRenderer),
                    Author: GetAuthor(membershipRenderer),
                    AuthorPhotoUrl: GetAuthorPhotoUrl(membershipRenderer),
                    MessageParts: ExtractMessageParts(header),
                    Amount: null,
                    StickerUrl: null
                ));
            }
            else if (item["liveChatPaidStickerRenderer"] is JsonNode stickerRenderer)
            {
                raw.Add(new RawEntry(
                    Type: "sticker",
                    OffsetMs: offsetMs,
                    TimestampUsec: GetTimestampUsec(stickerRenderer),
                    Author: GetAuthor(stickerRenderer),
                    AuthorPhotoUrl: GetAuthorPhotoUrl(stickerRenderer),
                    MessageParts: new List<MessagePart> { new MessagePart(false, "(유료 스티커)", null) },
                    Amount: stickerRenderer["purchaseAmountText"]?["simpleText"]?.GetValue<string>() ?? "",
                    StickerUrl: GetLastThumbnailUrl(stickerRenderer["sticker"]?["thumbnails"]?.AsArray())
                ));
            }
        }
    }

    // videoOffsetTimeMsec은 방송 시작 전 채팅에 대해 0으로 찍혀서 들어오는 경우가 많음.
    // offsetMs > 0인 첫 메시지를 기준으로 "방송 시작 시각(timestampUsec)"을 역산한 뒤,
    // 모든 메시지를 그 기준 시각과의 차이로 재계산한다. (시작 전 채팅 -> 음수)
    long? streamStartUsec = null;
    foreach (var r in raw)
    {
        if (r.OffsetMs is long om && om > 0)
        {
            streamStartUsec = r.TimestampUsec - om * 1000;
            break;
        }
    }

    var entries = new List<ChatEntry>();
    foreach (var r in raw)
    {
        long time = streamStartUsec is long start
            ? (r.TimestampUsec - start) / 1000
            : (r.OffsetMs ?? 0);

        entries.Add(new ChatEntry(
            r.Type, time, r.Author, r.AuthorPhotoUrl, r.MessageParts, r.Amount, r.StickerUrl));
    }

    return entries;
}

static string NormalizeUrl(string url)
{
    // 프로토콜 없이 "//yt3.ggpht.com/..." 형태로 오는 URL을 https:// 로 보정
    return url.StartsWith("//") ? "https:" + url : url;
}

static string? GetLastThumbnailUrl(JsonArray? thumbnails)
{
    if (thumbnails == null || thumbnails.Count == 0)
    {
        return null;
    }

    var url = thumbnails[^1]?["url"]?.GetValue<string>();
    return url == null ? null : NormalizeUrl(url);
}

static string GetAuthor(JsonNode renderer)
{
    return renderer["authorName"]?["simpleText"]?.GetValue<string>() ?? "(알수없음)";
}

static long GetTimestampUsec(JsonNode renderer)
{
    var node = renderer["timestampUsec"];
    if (node == null)
    {
        return 0;
    }
    long.TryParse(node.ToString(), out var value);
    return value;
}

static string? GetAuthorPhotoUrl(JsonNode renderer)
{
    return GetLastThumbnailUrl(renderer["authorPhoto"]?["thumbnails"]?.AsArray());
}

static List<MessagePart> ExtractMessageParts(JsonNode? message)
{
    var parts = new List<MessagePart>();
    if (message == null)
    {
        return parts;
    }

    var runs = message["runs"]?.AsArray();
    if (runs == null)
    {
        return parts;
    }

    foreach (var run in runs)
    {
        if (run?["text"] != null)
        {
            parts.Add(new MessagePart(false, run["text"]!.GetValue<string>(), null));
        }
        else if (run?["emoji"] != null)
        {
            var emoji = run["emoji"]!;
            var shortcuts = emoji["shortcuts"]?.AsArray();
            string alt = (shortcuts != null && shortcuts.Count > 0)
                ? shortcuts[0]!.GetValue<string>()
                : "";

            string? imgUrl = GetLastThumbnailUrl(emoji["image"]?["thumbnails"]?.AsArray());

            parts.Add(new MessagePart(true, alt, imgUrl));
        }
    }

    return parts;
}

static string PartsToPlainText(List<MessagePart> parts)
{
    var sb = new StringBuilder();
    foreach (var p in parts)
    {
        sb.Append(p.Text);
    }
    return sb.ToString();
}

static string PartsToHtml(List<MessagePart> parts)
{
    var sb = new StringBuilder();
    foreach (var p in parts)
    {
        if (p.IsEmoji && p.ImageUrl != null)
        {
            string alt = WebUtility.HtmlEncode(p.Text);
            sb.Append($"<img class=\"emoji\" src=\"{WebUtility.HtmlEncode(p.ImageUrl)}\" alt=\"{alt}\" title=\"{alt}\">");
        }
        else
        {
            sb.Append(WebUtility.HtmlEncode(p.Text));
        }
    }
    return sb.ToString();
}

static string MsToTimestamp(long ms)
{
    string sign = ms < 0 ? "-" : "";
    long totalSec = Math.Abs(ms) / 1000;
    long h = totalSec / 3600;
    long m = (totalSec % 3600) / 60;
    long s = totalSec % 60;

    return h > 0
        ? $"{sign}{h:D2}:{m:D2}:{s:D2}"
        : $"{sign}{m:D2}:{s:D2}";
}

static void WriteTextLog(List<ChatEntry> entries, string path)
{
    using var writer = new StreamWriter(path, false, Encoding.UTF8);

    foreach (var e in entries)
    {
        string ts = MsToTimestamp(e.Time);
        string message = PartsToPlainText(e.MessageParts);

        switch (e.Type)
        {
            case "superchat":
                writer.WriteLine($"[{ts}] 💰 {e.Author} ({e.Amount}): {message}");
                break;
            case "membership":
                writer.WriteLine($"[{ts}] ⭐ {e.Author}: {message}");
                break;
            case "sticker":
                writer.WriteLine($"[{ts}] 🎁 {e.Author} ({e.Amount}): {message}");
                break;
            default:
                writer.WriteLine($"[{ts}] {e.Author}: {message}");
                break;
        }
    }
}

static void WriteHtml(List<ChatEntry> entries, string path, string title)
{
    var rows = new StringBuilder();

    foreach (var e in entries)
    {
        string ts = MsToTimestamp(e.Time);
        string author = WebUtility.HtmlEncode(e.Author);
        string messageHtml = PartsToHtml(e.MessageParts);

        string avatarHtml = e.AuthorPhotoUrl != null
            ? $"<img class=\"avatar\" src=\"{WebUtility.HtmlEncode(e.AuthorPhotoUrl)}\" alt=\"\">"
            : "<span class=\"avatar avatar-placeholder\"></span>";

        string amountHtml = "";
        if (e.Type is "superchat" or "sticker")
        {
            amountHtml = $"<span class=\"amount\">{WebUtility.HtmlEncode(e.Amount ?? "")}</span>";
        }

        string stickerHtml = "";
        if (e.Type == "sticker" && e.StickerUrl != null)
        {
            stickerHtml = $"<img class=\"sticker\" src=\"{WebUtility.HtmlEncode(e.StickerUrl)}\" alt=\"sticker\">";
        }

        rows.Append($"""
            <div class="msg {e.Type}">
              {avatarHtml}
              <div class="bubble">
                <span class="time">{ts}</span>
                <span class="author">{author}{amountHtml}</span>
                <span class="text-content">{messageHtml}{stickerHtml}</span>
              </div>
            </div>

            """);
    }

    string document = $$"""
        <!DOCTYPE html>
        <html lang="ko">
        <head>
        <meta charset="utf-8">
        <title>{{WebUtility.HtmlEncode(title)}}</title>
        <style>
          :root {
            --bg: #ffffff;
            --fg: #1a1a1a;
            --time-color: #767676;
            --author-color: #1a73e8;
            --avatar-bg: #e0e0e0;
            --highlight-fg: #ffffff;
            --highlight-time: rgba(255,255,255,0.75);
            --superchat-bg: #1e88e5;
            --membership-bg: #2e7d32;
            --sticker-bg: #6a1b9a;
          }
          @media (prefers-color-scheme: dark) {
            :root {
              --bg: #0f0f0f;
              --fg: #e5e5e5;
              --time-color: #888;
              --author-color: #65b1ff;
              --avatar-bg: #333;
            }
          }
          body {
            font-family: sans-serif;
            background: var(--bg);
            color: var(--fg);
            margin: 0;
            padding: 1rem;
          }
          .chat {
            max-width: 720px;
            margin: 0 auto;
          }
          .msg {
            display: flex;
            gap: 0.5rem;
            padding: 0.3rem 0.6rem;
            align-items: flex-start;
            font-size: 0.92rem;
            line-height: 1.4;
          }
          .avatar {
            width: 24px;
            height: 24px;
            border-radius: 50%;
            flex-shrink: 0;
            margin-top: 0.15rem;
            object-fit: cover;
            background: var(--avatar-bg);
          }
          .avatar-placeholder {
            display: inline-block;
          }
          .bubble {
            display: flex;
            flex-wrap: wrap;
            align-items: baseline;
            gap: 0.4em;
            border-radius: 8px;
            padding: 0.15rem 0.5rem;
          }
          .msg.superchat .bubble { background: var(--superchat-bg); color: var(--highlight-fg); }
          .msg.membership .bubble { background: var(--membership-bg); color: var(--highlight-fg); }
          .msg.sticker .bubble { background: var(--sticker-bg); color: var(--highlight-fg); }
          .time {
            color: var(--time-color);
            font-size: 0.78rem;
            flex-shrink: 0;
          }
          .msg.superchat .time, .msg.membership .time, .msg.sticker .time {
            color: var(--highlight-time);
          }
          .author {
            font-weight: 600;
            color: var(--author-color);
            flex-shrink: 0;
          }
          .msg.superchat .author, .msg.membership .author, .msg.sticker .author {
            color: var(--highlight-fg);
          }
          .amount {
            font-weight: 700;
            margin-left: 0.4em;
          }
          .text-content {
            word-break: break-word;
          }
          .emoji {
            width: 1.2em;
            height: 1.2em;
            vertical-align: middle;
            display: inline-block;
          }
          .sticker {
            width: 64px;
            height: 64px;
            display: block;
            margin-top: 0.3rem;
          }
        </style>
        </head>
        <body>
        <div class="chat">
        {{rows}}
        </div>
        </body>
        </html>
        """;

    File.WriteAllText(path, document, Encoding.UTF8);
}

record MessagePart(bool IsEmoji, string Text, string? ImageUrl);

record RawEntry(
    string Type,
    long? OffsetMs,
    long TimestampUsec,
    string Author,
    string? AuthorPhotoUrl,
    List<MessagePart> MessageParts,
    string? Amount,
    string? StickerUrl
);

record ChatEntry(
    string Type,
    long Time,
    string Author,
    string? AuthorPhotoUrl,
    List<MessagePart> MessageParts,
    string? Amount,
    string? StickerUrl
);
