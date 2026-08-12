<script setup lang="ts">
/**
 * Both icons stay in the DOM and cross-fade, so the swap animates in and out
 * without a motion library. Which one is visible is decided purely by the
 * theme class on <html>, so there is no client state to mismatch on hydration.
 */
function toggle() {
  const root = document.documentElement
  const isDark = root.classList.contains('dark')

  root.classList.toggle('dark', !isDark)
  root.classList.toggle('light', isDark)

  try {
    localStorage.setItem('sickrgb-theme', isDark ? 'light' : 'dark')
  } catch {
    // Private browsing, or storage is full. The theme still applies for now.
  }
}
</script>

<template>
  <button class="icon-btn theme-toggle" type="button" aria-label="Switch theme" @click="toggle">
    <span class="theme-toggle__icons" aria-hidden="true">
      <svg
        class="theme-toggle__icon theme-toggle__icon--sun"
        viewBox="0 0 24 24"
        fill="none"
        stroke="currentColor"
        stroke-width="2"
        stroke-linecap="round"
      >
        <circle cx="12" cy="12" r="4" />
        <path
          d="M12 2v2M12 20v2M4.9 4.9l1.4 1.4M17.7 17.7l1.4 1.4M2 12h2M20 12h2M4.9 19.1l1.4-1.4M17.7 6.3l1.4-1.4"
        />
      </svg>
      <svg
        class="theme-toggle__icon theme-toggle__icon--moon"
        viewBox="0 0 24 24"
        fill="none"
        stroke="currentColor"
        stroke-width="2"
        stroke-linecap="round"
        stroke-linejoin="round"
      >
        <path d="M20 14.5A8.5 8.5 0 0 1 9.5 4a8.5 8.5 0 1 0 10.5 10.5Z" />
      </svg>
    </span>
  </button>
</template>

<style>
.theme-toggle__icons {
  position: relative;
  display: grid;
  place-items: center;
  width: 1.0625rem;
  height: 1.0625rem;
}

.theme-toggle__icon {
  position: absolute;
  width: 100%;
  height: 100%;
  transition-property: opacity, filter, scale;
  transition-duration: 300ms;
  transition-timing-function: var(--ease-state);
}

/* Hidden state: the exact contextual icon values, scale 0.25 and blur 4px. */
.theme-toggle__icon--moon,
:root.dark .theme-toggle__icon--sun {
  opacity: 0;
  scale: 0.25;
  filter: blur(4px);
}

.theme-toggle__icon--sun,
:root.dark .theme-toggle__icon--moon {
  opacity: 1;
  scale: 1;
  filter: blur(0);
}

/* Without JS no class is set, so follow the system preference instead. */
@media (prefers-color-scheme: dark) {
  :root:not(.light):not(.dark) .theme-toggle__icon--sun {
    opacity: 0;
    scale: 0.25;
    filter: blur(4px);
  }

  :root:not(.light):not(.dark) .theme-toggle__icon--moon {
    opacity: 1;
    scale: 1;
    filter: blur(0);
  }
}

@media (prefers-reduced-motion: reduce) {
  .theme-toggle__icon {
    transition-duration: 0.01ms;
  }
}
</style>
