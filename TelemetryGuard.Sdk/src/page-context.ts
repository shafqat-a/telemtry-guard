/**
 * SDK-09: page context the SERVER cannot see when the API is mounted on a
 * different origin than the page.
 *
 * Same-origin, the server reads the landing URL from `Referer` and the cookies off
 * the request, and none of this is needed — which is why SDK-02 deliberately said
 * "do not duplicate what the server already sees". Cross-origin that stops being
 * true: `strict-origin-when-cross-origin` trims `Referer` to the bare origin, and
 * `SameSite=Lax` cookies are not sent at all. Every utm_*, click id and cross-page
 * identifier silently disappears.
 *
 * So the SDK reports them itself, the way analytics vendors on third-party domains
 * have always had to. Two limits worth knowing:
 *   - `document.cookie` cannot see HttpOnly cookies (a site's login/session
 *     cookies). Nothing in the browser can. Same-origin capture still gets those.
 *   - Anything reported from here is client-supplied and therefore forgeable,
 *     unlike a header the server observed. Server-observed values take precedence
 *     when both exist.
 */

const MAX_URL = 1024;
const MAX_COOKIE_VALUE = 1024;
const MAX_COOKIES = 32;

function clamp(value: string, max: number): string {
  return value.length <= max ? value : value.slice(0, max);
}

/** The page's own URL, query string included — the thing UTMs and click ids ride in. */
export function pageUrl(): string {
  try {
    return clamp(location.href, MAX_URL);
  } catch {
    return '';
  }
}

/**
 * The page's referrer — where the visitor actually came from. Distinct from the
 * `Referer` on the beacon request, which is the tagged page itself; that is why an
 * untagged visit currently records as `direct` even when it came from a search engine.
 */
export function pageReferrer(): string {
  try {
    return clamp(document.referrer || '', MAX_URL);
  } catch {
    return '';
  }
}

/**
 * Every cookie this script can read. HttpOnly ones are invisible by definition; the
 * identifiers that follow a visitor across pages (`_ga`, `_fbp`, `_fbc`, `_gcl_aw`,
 * `_ttp`, our own `tg_fp`) are all set by JavaScript, so they are all readable.
 */
export function readableCookies(): Record<string, string> {
  const out: Record<string, string> = {};
  try {
    const raw = document.cookie;
    if (!raw) return out;

    const parts = raw.split(';');
    for (let i = 0; i < parts.length && Object.keys(out).length < MAX_COOKIES; i++) {
      const part = parts[i]!;
      const eq = part.indexOf('=');
      if (eq <= 0) continue;

      const name = part.slice(0, eq).trim();
      if (!name) continue;

      let value = part.slice(eq + 1).trim();
      try {
        value = decodeURIComponent(value); // match what the server sees on same-origin
      } catch {
        /* malformed escape: keep the raw value rather than dropping the cookie */
      }
      if (value) out[name] = clamp(value, MAX_COOKIE_VALUE);
    }
  } catch {
    /* document.cookie can throw in sandboxed frames — never break the flush */
  }
  return out;
}
