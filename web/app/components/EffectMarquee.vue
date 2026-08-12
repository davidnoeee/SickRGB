<script setup lang="ts">
import { ref } from 'vue'

/**
 * The full effect list as a moving strip, each card carrying a live sketch of
 * what that effect does.
 *
 * Motion that runs longer than five seconds needs a way to stop it, so there
 * is a real pause control, and it also pauses on hover and on keyboard focus.
 * Under reduced motion the strip does not move at all and becomes an ordinary
 * horizontal scroller.
 */

const effects = [
  { id: 'static', name: 'Static' },
  { id: 'palette', name: 'Palette' },
  { id: 'gradient', name: 'Gradient' },
  { id: 'breathing', name: 'Breathing' },
  { id: 'rainbow', name: 'Rainbow Cycle' },
  { id: 'colorwave', name: 'Colour Wave' },
  { id: 'ripple', name: 'Ripple' },
  { id: 'wave', name: 'Reactive Wave' },
  { id: 'flash', name: 'Reactive Flash' },
  { id: 'heat', name: 'Activity Heat' },
  { id: 'audio', name: 'Music Visualiser' },
  { id: 'direction', name: 'Directional Sound' },
  { id: 'screen', name: 'Screen Ambient' },
]

const paused = ref(false)
</script>

<template>
  <div class="marquee" :class="{ 'is-paused': paused }">
    <!-- Focusable because under reduced motion this becomes a scroller, and
         not every browser makes an overflow container keyboard reachable.
         Focus also pauses the strip, which is a fair bonus. -->
    <div class="marquee__viewport" tabindex="0" role="group" aria-label="All effects">
      <div class="marquee__rail">
        <ul class="marquee__set">
          <li v-for="effect in effects" :key="effect.id" class="effect-card">
            <EffectPreview :effect="effect.id" />
            <span class="effect-card__label">{{ effect.name }}</span>
          </li>
        </ul>

        <ul class="marquee__set" aria-hidden="true">
          <li v-for="effect in effects" :key="`${effect.id}-echo`" class="effect-card">
            <EffectPreview :effect="effect.id" />
            <span class="effect-card__label">{{ effect.name }}</span>
          </li>
        </ul>
      </div>
    </div>

    <div class="marquee__footer">
      <button class="marquee__toggle" type="button" @click="paused = !paused">
        <svg
          v-if="paused"
          class="btn__icon marquee__play"
          viewBox="0 0 24 24"
          fill="currentColor"
          aria-hidden="true"
        >
          <path d="M8 5.5v13l11-6.5z" />
        </svg>
        <svg v-else class="btn__icon" viewBox="0 0 24 24" fill="currentColor" aria-hidden="true">
          <path d="M8 5h3v14H8zM13 5h3v14h-3z" />
        </svg>
        {{ paused ? 'Play' : 'Pause' }}
      </button>
    </div>
  </div>
</template>
