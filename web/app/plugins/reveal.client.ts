/**
 * Reveals sections as they scroll into view.
 *
 * The hidden state lives behind `html.js`, so if this never runs the page is
 * simply visible. Each element is unobserved once shown: a reveal is a one
 * time entrance, not something that replays every time you scroll past.
 */
export default defineNuxtPlugin((nuxtApp) => {
  nuxtApp.hook('app:mounted', () => {
    const elements = Array.from(document.querySelectorAll<HTMLElement>('[data-reveal]'))
    if (elements.length === 0) return

    if (!('IntersectionObserver' in window)) {
      for (const element of elements) element.classList.add('is-visible')
      return
    }

    const observer = new IntersectionObserver(
      (entries) => {
        for (const entry of entries) {
          if (!entry.isIntersecting) continue
          entry.target.classList.add('is-visible')
          observer.unobserve(entry.target)
        }
      },
      { threshold: 0.08, rootMargin: '0px 0px -8% 0px' },
    )

    for (const element of elements) observer.observe(element)
  })
})
