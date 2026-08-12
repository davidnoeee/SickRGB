<script setup lang="ts">
import { computed } from 'vue'

const REPO = 'https://github.com/davidnoeee/SickRGB'

// Resolved on the server and revalidated hourly, so the version and file size
// on this page follow the GitHub release without anyone editing the site.
const { data: release } = await useFetch('/api/release')

const version = computed(() => release.value?.version ?? null)
const downloadUrl = computed(() => release.value?.downloadUrl ?? `${REPO}/releases/latest`)
const releaseUrl = computed(() => release.value?.releaseUrl ?? `${REPO}/releases/latest`)
const assetName = computed(() => release.value?.assetName ?? 'SickRGB.exe')

const sizeLabel = computed(() => {
  const bytes = release.value?.size
  if (typeof bytes !== 'number' || bytes <= 0) return null
  return `${(bytes / 1024 / 1024).toFixed(1)} MB`
})

const starsLabel = computed(() => {
  const stars = release.value?.stars
  // A zero is worse than no number at all, so show nothing until there is one.
  if (typeof stars !== 'number' || stars <= 0) return null
  return stars >= 1000 ? `${(stars / 1000).toFixed(1)}k` : String(stars)
})

const dateLabel = computed(() => {
  const iso = release.value?.publishedAt
  if (!iso) return null
  const date = new Date(iso)
  if (Number.isNaN(date.getTime())) return null
  // Fixed locale and time zone, so the server and the client agree.
  return new Intl.DateTimeFormat('en-GB', {
    day: 'numeric',
    month: 'short',
    year: 'numeric',
    timeZone: 'UTC',
  }).format(date)
})

const features = [
  '1,400+ devices supported',
  '13 effects',
  'One file, runtime bundled',
  'No administrator rights',
  'No telemetry',
]

const heroMeta = computed(() =>
  [version.value, sizeLabel.value, 'Windows 10 and 11'].filter((item): item is string =>
    Boolean(item),
  ),
)

// Social cards need absolute URLs. Building them from the request means the
// card works on any domain without a hardcoded host to keep in sync.
const requestUrl = useRequestURL()
const ogImage = new URL('/og.png', requestUrl.origin).href

useHead({
  meta: [
    { property: 'og:site_name', content: 'SickRGB' },
    { property: 'og:url', content: requestUrl.href },
    { property: 'og:image', content: ogImage },
    { property: 'og:image:width', content: '2400' },
    { property: 'og:image:height', content: '1260' },
    {
      property: 'og:image:alt',
      content:
        'SickRGB. Every RGB light on your desk, in one shared space. A field of lights with a ring of colour spreading outward from the centre.',
    },
    { name: 'twitter:card', content: 'summary_large_image' },
    { name: 'twitter:image', content: ogImage },
  ],
})
</script>

<template>
  <a class="skip-link" href="#main">Skip to content</a>

  <GridField />

  <header class="site-header">
    <div class="shell site-header__inner">
      <a class="wordmark" href="#top">
        <BrandMark class="wordmark__mark" />
        SickRGB
      </a>

      <nav class="site-nav" aria-label="Primary">
        <a href="#how">How it works</a>
        <a href="#effects">Effects</a>
        <a href="#install">Install</a>
      </nav>

      <div class="site-header__actions">
        <ThemeToggle />
        <a class="btn btn--primary" :href="downloadUrl">Download</a>
      </div>
    </div>
  </header>

  <main id="main" tabindex="-1">
    <section id="top" class="hero shell">
      <h1 class="hero__title enter enter-1">Every RGB light on your desk, in one shared space.</h1>

      <p class="hero__lead enter enter-2">
        Every light you own, from your motherboard to your keyboard, sits on one canvas with a real
        position in millimetres. Animations stay in step because the timing comes from the actual
        distance between them.
      </p>

      <div class="hero__actions enter enter-3">
        <a class="btn btn--primary btn--has-leading-icon" :href="downloadUrl">
          <!-- The Windows 11 mark: four flat, evenly gapped squares. -->
          <svg class="btn__icon" viewBox="0 0 24 24" fill="currentColor" aria-hidden="true">
            <path d="M2 2h9v9H2zM13 2h9v9h-9zM2 13h9v9H2zM13 13h9v9h-9z" />
          </svg>
          Download for Windows
        </a>

        <a class="btn btn--secondary" :href="REPO">
          <svg class="btn__icon" viewBox="0 0 24 24" fill="currentColor" aria-hidden="true">
            <path
              d="M12 1.5a10.5 10.5 0 0 0-3.32 20.47c.53.1.72-.23.72-.5v-1.8c-2.92.63-3.54-1.4-3.54-1.4-.48-1.22-1.17-1.54-1.17-1.54-.95-.66.07-.64.07-.64 1.06.07 1.61 1.09 1.61 1.09.94 1.6 2.46 1.14 3.06.87.1-.68.37-1.14.66-1.4-2.33-.27-4.78-1.17-4.78-5.2 0-1.15.41-2.09 1.08-2.83-.1-.27-.47-1.34.1-2.8 0 0 .88-.28 2.89 1.08a9.98 9.98 0 0 1 5.26 0c2-1.36 2.88-1.08 2.88-1.08.58 1.46.21 2.53.11 2.8.67.74 1.08 1.68 1.08 2.83 0 4.04-2.46 4.93-4.8 5.19.38.33.72.97.72 1.96v2.9c0 .28.19.61.72.5A10.5 10.5 0 0 0 12 1.5Z"
            />
          </svg>
          Star on GitHub
          <span v-if="starsLabel" class="btn__count tnum">{{ starsLabel }}</span>
        </a>
      </div>

      <ul class="hero__meta enter enter-4">
        <li v-for="item in heroMeta" :key="item" :class="{ tnum: item === version }">{{ item }}</li>
      </ul>

      <!-- Not decoration for its own sake: the background canvas draws the
           keyboard, mouse and pad into this box, so the hero ends on the thing
           the app is actually for. Empty here because the drawing belongs to
           one canvas, and the box is what tells it where to put them. -->
      <div class="hero__desk" aria-hidden="true" />
    </section>

    <section id="how" class="section shell">
      <div class="section__head" data-reveal>
        <h2>One canvas, not five. One app.</h2>
        <p>
          Most RGB software animates each device on its own, and every brand wants you to install
          its own program to do it. This is one window, and one shared space.
        </p>
      </div>

      <div class="split" data-reveal="2">
        <AppPanel />

        <ol class="points">
          <li>
            <h3>Real distance</h3>
            <p>
              Drag your devices on the Layout page to match your desk. Effects are functions of
              position and time, so moving one changes the timing straight away.
            </p>
          </li>
          <li>
            <h3>1,400+ devices, out of the box</h3>
            <p>
              Motherboards, memory, graphics cards, coolers and fans from every major brand are
              recognised without you configuring anything. Supported keyboards and mice get native
              drivers on top.
            </p>
          </li>
          <li>
            <h3>Straight about input</h3>
            <p>
              Reactive effects need a system input hook. It runs only while one is active, turns
              each press into a position, and drops everything else.
            </p>
          </li>
        </ol>
      </div>
    </section>

    <section id="effects" class="section">
      <div class="shell">
        <div class="section__head" data-reveal>
          <h2>12+ effects.</h2>
          <p>
            Set one for everything, or give any device its own. They share the same canvas either
            way, so the result stays spatially coherent.
          </p>
        </div>
      </div>

      <EffectMarquee />
    </section>

    <section id="install" class="section shell">
      <div class="install">
        <div data-reveal>
          <div class="section__head section__head--tight">
            <h2>Install</h2>
          </div>

          <ol class="steps">
            <li class="step">
              <div>
                <strong>Download <code>{{ assetName }}</code></strong>
                <p>From the latest release on GitHub.</p>
              </div>
            </li>
            <li class="step">
              <div>
                <strong>Run it</strong>
                <p>There is nothing else to set up. Settings live in your AppData folder.</p>
              </div>
            </li>
            <li class="step">
              <div>
                <strong>Arrange your desk</strong>
                <p>Drag your devices on the Layout page until the distances match the real thing.</p>
              </div>
            </li>
          </ol>

          <p class="fineprint">
            No account, no analytics, no telemetry. Nothing about you or what you type is
            collected, stored or sent anywhere, and the only machine it talks to is your own.
          </p>
        </div>

        <aside class="release" data-reveal="2">
          <span class="eyebrow">Latest release</span>
          <p class="release__version tnum">{{ version ?? 'Latest' }}</p>

          <ul class="release__meta">
            <li v-if="sizeLabel" class="tnum">{{ sizeLabel }}</li>
            <li v-if="dateLabel" class="tnum">{{ dateLabel }}</li>
          </ul>

          <ul class="release__features">
            <li v-for="feature in features" :key="feature">
              <svg
                class="release__check"
                viewBox="0 0 24 24"
                fill="none"
                stroke="currentColor"
                stroke-width="2.25"
                stroke-linecap="round"
                stroke-linejoin="round"
                aria-hidden="true"
              >
                <path d="M4 12.5 9.5 18 20 6.5" />
              </svg>
              <span class="tnum">{{ feature }}</span>
            </li>
          </ul>

          <a class="btn btn--primary release__cta btn--has-trailing-icon" :href="downloadUrl">
            Download {{ assetName }}
            <svg
              class="btn__icon"
              viewBox="0 0 24 24"
              fill="none"
              stroke="currentColor"
              stroke-width="2"
              stroke-linecap="round"
              stroke-linejoin="round"
              aria-hidden="true"
            >
              <path d="M12 3v12M7 11l5 5 5-5M4 20h16" />
            </svg>
          </a>

          <a class="release__notes" :href="releaseUrl">Read the release notes</a>
        </aside>
      </div>
    </section>
  </main>

  <footer class="site-footer">
    <div class="shell">
      <div class="site-footer__top">
        <div class="site-footer__brand">
          <span class="wordmark">
            <BrandMark class="wordmark__mark" />
            SickRGB
          </span>
          <p>
            RGB lighting for Windows that treats every device you own as lights in one shared
            space.
          </p>
        </div>

        <div class="site-footer__cols">
          <nav aria-labelledby="footer-product">
            <p id="footer-product" class="site-footer__title">Product</p>
            <ul>
              <li><a href="#how">How it works</a></li>
              <li><a href="#effects">Effects</a></li>
              <li><a href="#install">Install</a></li>
            </ul>
          </nav>

          <nav aria-labelledby="footer-source">
            <p id="footer-source" class="site-footer__title">Source</p>
            <ul>
              <li><a :href="REPO">GitHub</a></li>
              <li><a :href="`${REPO}/blob/main/PROTOCOL.md`">Protocol notes</a></li>
              <li><a :href="`${REPO}/blob/main/CONTRIBUTING.md`">Contributing</a></li>
            </ul>
          </nav>

          <nav aria-labelledby="footer-release">
            <p id="footer-release" class="site-footer__title">Release</p>
            <ul>
              <li><a :href="downloadUrl">Download SickRGB</a></li>
              <li><a :href="releaseUrl">Release notes</a></li>
              <li><a :href="`${REPO}/blob/main/LICENSE`">Licence</a></li>
            </ul>
          </nav>
        </div>
      </div>

      <div class="site-footer__bottom">
        <span>&copy; 2026 David No&eacute;</span>
        <a :href="`${REPO}/blob/main/LICENSE`">PolyForm Noncommercial 1.0.0</a>
      </div>
    </div>
  </footer>
</template>
