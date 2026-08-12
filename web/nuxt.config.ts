export default defineNuxtConfig({
  compatibilityDate: '2026-08-12',
  ssr: true,
  devtools: { enabled: false },

  css: ['~/assets/css/main.css'],

  runtimeConfig: {
    // Server only. Override with NUXT_GITHUB_TOKEN. Optional: without it the
    // release lookup just uses GitHub's unauthenticated rate limit.
    githubToken: '',
  },

  app: {
    head: {
      htmlAttrs: { lang: 'en' },
      title: 'SickRGB - every RGB light on your desk, in one shared space',
      meta: [
        { charset: 'utf-8' },
        { name: 'viewport', content: 'width=device-width, initial-scale=1' },
        {
          name: 'description',
          content:
            'RGB lighting for Windows. Every light you own, from your motherboard to your keyboard, on one canvas, with animations timed by the real distance between them.',
        },
        { name: 'theme-color', content: '#fcfcfc', media: '(prefers-color-scheme: light)' },
        { name: 'theme-color', content: '#0c0c0d', media: '(prefers-color-scheme: dark)' },
        { property: 'og:type', content: 'website' },
        { property: 'og:title', content: 'SickRGB' },
        {
          property: 'og:description',
          content:
            'RGB lighting for Windows that treats every device as lights in one shared space.',
        },
      ],
      link: [
        // The app's own icon, so the tab matches the binary.
        { rel: 'icon', type: 'image/png', href: '/icon.png' },
        { rel: 'apple-touch-icon', href: '/icon.png' },
        { rel: 'preconnect', href: 'https://fonts.googleapis.com' },
        { rel: 'preconnect', href: 'https://fonts.gstatic.com', crossorigin: '' },
        {
          rel: 'stylesheet',
          href: 'https://fonts.googleapis.com/css2?family=Inter:opsz,wght@14..32,400..700&display=swap',
        },
      ],
      script: [
        {
          // Resolve the theme before first paint so the page never flashes the
          // wrong appearance, and mark that JS is available so scroll reveals
          // only hide themselves when something can show them again.
          innerHTML: `(function(){try{var d=document.documentElement;d.classList.add('js');var t=localStorage.getItem('sickrgb-theme');if(t!=='light'&&t!=='dark'){t=window.matchMedia('(prefers-color-scheme: dark)').matches?'dark':'light'}d.classList.add(t)}catch(e){}})()`,
          tagPosition: 'head',
        },
      ],
    },
  },

  nitro: {
    compressPublicAssets: true,
  },
})
