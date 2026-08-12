/**
 * Latest release, resolved on the server and cached for an hour.
 *
 * Keeping this server side means the page ships the real version number in its
 * HTML: no client request, no layout shift, and it still works with JS off.
 * Every failure path falls back to the permanent /releases/latest URL, so the
 * download button is never dead.
 */

const REPO = 'davidnoeee/SickRGB'
const RELEASES_URL = `https://github.com/${REPO}/releases/latest`

interface GithubAsset {
  name?: string
  size?: number
  browser_download_url?: string
}

interface GithubRelease {
  tag_name?: string
  html_url?: string
  published_at?: string
  assets?: GithubAsset[]
}

interface GithubRepo {
  stargazers_count?: number
}

export interface ReleaseInfo {
  version: string | null
  downloadUrl: string
  releaseUrl: string
  assetName: string | null
  size: number | null
  publishedAt: string | null
  stars: number | null
}

const FALLBACK: ReleaseInfo = {
  version: null,
  downloadUrl: RELEASES_URL,
  releaseUrl: RELEASES_URL,
  assetName: null,
  size: null,
  publishedAt: null,
  stars: null,
}

export default defineCachedEventHandler(
  async (): Promise<ReleaseInfo> => {
    const headers: Record<string, string> = {
      Accept: 'application/vnd.github+json',
      'User-Agent': 'sickrgb-site',
    }

    // A token lifts the unauthenticated rate limit, but is entirely optional.
    const token = process.env.GITHUB_TOKEN
    if (token) headers.Authorization = `Bearer ${token}`

    const [release, repo] = await Promise.all([
      $fetch<GithubRelease>(`https://api.github.com/repos/${REPO}/releases/latest`, {
        headers,
      }).catch(() => null),
      $fetch<GithubRepo>(`https://api.github.com/repos/${REPO}`, { headers }).catch(() => null),
    ])

    if (!release) {
      return {
        ...FALLBACK,
        stars: typeof repo?.stargazers_count === 'number' ? repo.stargazers_count : null,
      }
    }

    const exe = release.assets?.find((asset) => asset.name?.toLowerCase().endsWith('.exe'))

    return {
      version: release.tag_name ?? null,
      downloadUrl: exe?.browser_download_url ?? release.html_url ?? RELEASES_URL,
      releaseUrl: release.html_url ?? RELEASES_URL,
      assetName: exe?.name ?? null,
      size: typeof exe?.size === 'number' ? exe.size : null,
      publishedAt: release.published_at ?? null,
      stars: typeof repo?.stargazers_count === 'number' ? repo.stargazers_count : null,
    }
  },
  {
    name: 'github-release',
    getKey: () => 'latest',
    maxAge: 60 * 60,
    swr: true,
  },
)
