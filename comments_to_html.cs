// comments_to_html.cs
//
// yt-dlp로 받은 *.info.json (--write-comments 옵션 포함) 파일에서
// 댓글(comments)을 추출해 유튜브 댓글창에 가까운 모양의 HTML로 변환합니다.
//
// 실행 (.NET 10 SDK, file-based apps):
//   dotnet run comments_to_html.cs -- "영상.info.json"
//   dotnet run comments_to_html.cs -- *.info.json
//
// 출력:
//   "영상.comments.html"
//
// 참고: 프로필 사진은 yt3.ggpht.com 등 유튜브 CDN에서 직접 불러오므로,
// 인터넷에 연결된 상태에서 열어야 이미지가 정상적으로 표시됩니다.

using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

if (args.Length < 1)
{
    Console.WriteLine("사용법: dotnet run comments_to_html.cs -- <파일1.info.json> [파일2.info.json ...]");
    Console.WriteLine("        dotnet run comments_to_html.cs -- *.info.json");
    return;
}

// PowerShell/cmd 등에서는 와일드카드가 셸에서 확장되지 않고
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

    Console.WriteLine($"== {inPath} ==");

    JsonNode? root;
    using (var stream = File.OpenRead(inPath))
    {
        root = JsonNode.Parse(stream);
    }

    if (root == null)
    {
        Console.WriteLine("[건너뜀] JSON 파싱에 실패했습니다.");
        continue;
    }

    string title = root["title"]?.GetValue<string>() ?? "댓글";
    var commentsNode = root["comments"] as JsonArray;

    if (commentsNode == null || commentsNode.Count == 0)
    {
        Console.WriteLine("[건너뜀] 댓글이 없습니다. --write-comments 옵션으로 받은 info.json인지 확인하세요.");
        continue;
    }

    var comments = commentsNode
        .Where(c => c != null)
        .Select(c => new CommentInfo(
            Id: c!["id"]?.GetValue<string>() ?? Guid.NewGuid().ToString(),
            Parent: c["parent"]?.GetValue<string>() ?? "root",
            Author: c["author"]?.GetValue<string>() ?? "(알수없음)",
            AuthorUrl: c["author_url"]?.GetValue<string>(),
            AuthorThumbnail: GetAuthorThumbnail(c),
            AuthorIsUploader: c["author_is_uploader"]?.GetValue<bool?>() ?? false,
            Text: c["text"]?.GetValue<string>() ?? "",
            Likes: GetLong(c["like_count"]),
            IsPinned: c["is_pinned"]?.GetValue<bool?>() ?? false,
            IsFavorited: c["is_favorited"]?.GetValue<bool?>() ?? false
        ))
        .ToList();

    Console.WriteLine($"총 {comments.Count}개 댓글 파싱 완료");

    string stem = Path.GetFileName(inPath);
    foreach (var suffix in new[] { ".info.json", ".json" })
    {
        if (stem.EndsWith(suffix, StringComparison.Ordinal))
        {
            stem = stem[..^suffix.Length];
            break;
        }
    }

    string dir = Path.GetDirectoryName(Path.GetFullPath(inPath))!;
    string htmlOut = Path.Combine(dir, $"{stem}.comments.html");

    WriteHtml(comments, title, htmlOut);

    Console.WriteLine($"HTML 뷰어: {htmlOut}");
    Console.WriteLine();
}

// ----- 이하 로컬 함수 -----

static long GetLong(JsonNode? node)
{
    if (node == null)
    {
        return 0;
    }

    try
    {
        return node.GetValue<long>();
    }
    catch
    {
        return (long)(node.GetValue<double?>() ?? 0);
    }
}

static string NormalizeUrl(string url)
{
    return url.StartsWith("//") ? "https:" + url : url;
}

static string? GetAuthorThumbnail(JsonNode comment)
{
    // 신버전 yt-dlp: author_thumbnails 배열 (해상도별)
    var thumbs = comment["author_thumbnails"]?.AsArray();
    if (thumbs != null && thumbs.Count > 0)
    {
        var url = thumbs[^1]?["url"]?.GetValue<string>();
        if (url != null)
        {
            return NormalizeUrl(url);
        }
    }

    // 구버전 / 단일 URL 필드
    var single = comment["author_thumbnail"]?.GetValue<string>();
    return single != null ? NormalizeUrl(single) : null;
}

// 작성자 이름에서 결정적인 색상을 뽑아 기본 아바타(이니셜) 배경색으로 사용
static string AvatarColor(string author)
{
    int hash = 0;
    foreach (char ch in author)
    {
        hash = (hash * 31 + ch) & 0x7fffffff;
    }
    int hue = hash % 360;
    return $"hsl({hue}, 45%, 45%)";
}

static string FormatLikes(long likes)
{
    if (likes <= 0)
    {
        return "";
    }
    if (likes >= 10000)
    {
        return $"{likes / 10000.0:0.#}만";
    }
    if (likes >= 1000)
    {
        return $"{likes / 1000.0:0.#}천";
    }
    return likes.ToString();
}


// :shortcode: 형태의 유튜브 커스텀 이모지를 시각적 뱃지로 렌더링한다.
// 텍스트 전체를 HTML-encode하되, 이모지 숏코드는 <span class="yt-emoji"> 태그로 감싼다.
static string RenderText(string raw)
{
    // :숏코드: 패턴을 찾아 span으로 교체한다.
    // 숏코드는 영문 소문자, 숫자, 하이픈으로 구성된다.
    var result = new StringBuilder();
    int pos = 0;
    foreach (Match m in Regex.Matches(raw, @":([\w-]+):"))
    {
        // 앞쪽 일반 텍스트 부분을 HTML-encode해서 추가
        if (m.Index > pos)
        {
            result.Append(WebUtility.HtmlEncode(raw[pos..m.Index]));
        }
        // 이모지 숏코드를 span으로 감싸서 추가
        result.Append($"""<span class="yt-emoji">{WebUtility.HtmlEncode(m.Groups[1].Value)}</span>""");
        pos = m.Index + m.Length;
    }
    // 나머지 텍스트 추가
    if (pos < raw.Length)
    {
        result.Append(WebUtility.HtmlEncode(raw[pos..]));
    }
    return result.ToString();
}

static string RenderComment(CommentInfo c, Dictionary<string, CommentInfo> byId,
    Dictionary<string, List<string>> children, int depth)
{
    string avatarHtml = c.AuthorThumbnail != null
        ? $"<img class=\"avatar\" src=\"{WebUtility.HtmlEncode(c.AuthorThumbnail)}\" alt=\"\" loading=\"lazy\">"
        : $"""<div class="avatar avatar-placeholder" style="background:{AvatarColor(c.Author)}">{WebUtility.HtmlEncode(c.Author.Length > 0 ? c.Author[..1].ToUpperInvariant() : "?")}</div>""";

    string authorHtml = c.AuthorUrl != null
        ? $"<a class=\"author\" href=\"{WebUtility.HtmlEncode(NormalizeUrl(c.AuthorUrl))}\" target=\"_blank\" rel=\"noopener\">{WebUtility.HtmlEncode(c.Author)}</a>"
        : $"<span class=\"author\">{WebUtility.HtmlEncode(c.Author)}</span>";

    string pinnedBadge = c.IsPinned ? """<span class="pinned">📌 고정된 댓글</span>""" : "";

    // time_text는 "n개월 전" 같은 상대 시간으로, 다운로드 시점 기준이라 나중에 보면
    // 부정확해진다. timestamp가 있으면 항상 절대 날짜로 표시한다.

    string likeHtml = c.Likes > 0
        ? $"""<span class="like"><svg viewBox="0 0 24 24"><path d="M1 21h4V9H1v12zm22-11c0-1.1-.9-2-2-2h-6.31l.95-4.57.03-.32c0-.41-.17-.79-.44-1.06L14.17 1 7.59 7.59C7.22 7.95 7 8.45 7 9v10c0 1.1.9 2 2 2h9c.83 0 1.54-.5 1.84-1.22l3.02-7.05c.09-.23.14-.47.14-.73v-2.18l-.01-.01.01-.01z"/></svg>{FormatLikes(c.Likes)}</span>"""
        : "";

    string heartHtml = c.IsFavorited
        ? """<span class="heart" title="크리에이터가 좋아함">❤️</span>"""
        : "";

    var childIds = children.TryGetValue(c.Id, out var list) ? list : new List<string>();

    string repliesHtml = "";
    if (childIds.Count > 0)
    {
        var sb = new StringBuilder();
        foreach (var childId in childIds)
        {
            sb.Append(RenderComment(byId[childId], byId, children, depth + 1));
        }

        repliesHtml = $"""
            <div class="reply-list">
            {sb}
            </div>

            """;
    }

    return $"""
        <div class="comment-thread{(depth == 0 ? "" : " is-reply")}">
          <div class="comment{(c.IsPinned ? " is-pinned" : "")}">
            {avatarHtml}
            <div class="comment-body">
              <div class="header">
                {authorHtml}
                {pinnedBadge}
              </div>
              <div class="text">{RenderText(c.Text)}</div>
              <div class="actions">
                {likeHtml}
                {heartHtml}
              </div>
            </div>
          </div>
        {repliesHtml}</div>

        """;
}

static void WriteHtml(List<CommentInfo> comments, string title, string path)
{
    var byId = comments.ToDictionary(c => c.Id);
    var children = new Dictionary<string, List<string>>();
    var rootIds = new List<string>();

    foreach (var c in comments)
    {
        if (c.Parent == "root" || !byId.ContainsKey(c.Parent))
        {
            rootIds.Add(c.Id);
        }
        else
        {
            if (!children.TryGetValue(c.Parent, out var list))
            {
                list = new List<string>();
                children[c.Parent] = list;
            }
            list.Add(c.Id);
        }
    }

    // 고정 댓글 먼저, 그 다음 좋아요 수 내림차순
    rootIds = rootIds
        .OrderByDescending(id => byId[id].IsPinned)
        .ThenByDescending(id => byId[id].Likes)
        .ToList();

    var body = new StringBuilder();
    foreach (var id in rootIds)
    {
        body.Append(RenderComment(byId[id], byId, children, 0));
    }

    string document = $$"""
        <!DOCTYPE html>
        <html lang="ko">
        <head>
        <meta charset="utf-8">
        <title>{{WebUtility.HtmlEncode(title)}} - 댓글</title>
        <style>
          :root {
            --bg: #ffffff;
            --fg: #0f0f0f;
            --secondary: #606060;
            --border: #e5e5e5;
            --hover: #f2f2f2;
            --pinned-bg: #fafafa;
            --avatar-bg: #ccc;
          }
          @media (prefers-color-scheme: dark) {
            :root {
              --bg: #0f0f0f;
              --fg: #f1f1f1;
              --secondary: #aaaaaa;
              --border: #2f2f2f;
              --hover: #1f1f1f;
              --pinned-bg: #1a1a1a;
              --avatar-bg: #444;
            }
          }
          * { box-sizing: border-box; }
          body {
            font-family: "Roboto", "Noto Sans KR", Arial, sans-serif;
            background: var(--bg);
            color: var(--fg);
            margin: 0;
            padding: 1.5rem;
          }
          .page {
            max-width: 760px;
            margin: 0 auto;
          }
          h1 {
            font-size: 1.4rem;
            font-weight: 500;
            margin: 0 0 0.25rem;
          }
          .count {
            color: var(--secondary);
            font-size: 0.9rem;
            margin: 0 0 1.25rem;
          }
          .comment-thread {
            margin-bottom: 1.1rem;
          }
          .comment {
            display: flex;
            gap: 12px;
            align-items: flex-start;
          }
          .comment.is-pinned {
            background: var(--pinned-bg);
            border-radius: 12px;
            padding: 8px;
            margin: -8px -8px 0 -8px;
          }
          .avatar {
            width: 40px;
            height: 40px;
            border-radius: 50%;
            flex-shrink: 0;
            object-fit: cover;
            background: var(--avatar-bg);
          }
          .avatar-placeholder {
            display: flex;
            align-items: center;
            justify-content: center;
            color: #fff;
            font-weight: 700;
            font-size: 1rem;
          }
          .comment-body {
            flex: 1;
            min-width: 0;
          }
          .header {
            display: flex;
            align-items: center;
            flex-wrap: wrap;
            gap: 6px;
            font-size: 0.8125rem;
            line-height: 1.4rem;
          }
          .author {
            font-weight: 500;
            color: var(--fg);
            text-decoration: none;
          }
          .author:hover {
            text-decoration: underline;
          }
          .pinned {
            color: var(--secondary);
            font-size: 0.75rem;
            font-weight: 500;
          }
          .text {
            font-size: 0.875rem;
            line-height: 1.4rem;
            white-space: pre-wrap;
            word-break: break-word;
            margin-top: 2px;
          }
          .actions {
            display: flex;
            align-items: center;
            gap: 12px;
            margin-top: 6px;
            min-height: 1px;
          }
          .like {
            display: inline-flex;
            align-items: center;
            gap: 6px;
            color: var(--secondary);
            font-size: 0.8125rem;
          }
          .like svg {
            width: 16px;
            height: 16px;
            fill: var(--secondary);
          }
          .heart {
            font-size: 0.9rem;
          }
          .yt-emoji {
            display: inline-block;
            background: var(--hover);
            color: var(--secondary);
            font-size: 0.7rem;
            font-weight: 600;
            line-height: 1;
            padding: 2px 5px;
            border-radius: 4px;
            vertical-align: middle;
            letter-spacing: 0;
          }
          .reply-list {
            margin-left: 52px;
            margin-top: 8px;
          }
          .comment-thread.is-reply .avatar,
          .comment-thread.is-reply .avatar-placeholder {
            width: 24px;
            height: 24px;
            font-size: 0.75rem;
          }
          .comment-thread.is-reply .reply-list {
            margin-left: 36px;
          }
        </style>
        </head>
        <body>
        <div class="page">
          <h1>{{WebUtility.HtmlEncode(title)}}</h1>
          <p class="count">댓글 {{comments.Count:N0}}개</p>
        {{body}}
        </div>
        </body>
        </html>
        """;

    File.WriteAllText(path, document, Encoding.UTF8);
}

record CommentInfo(
    string Id,
    string Parent,
    string Author,
    string? AuthorUrl,
    string? AuthorThumbnail,
    bool AuthorIsUploader,
    string Text,
    long Likes,
    bool IsPinned,
    bool IsFavorited
);
