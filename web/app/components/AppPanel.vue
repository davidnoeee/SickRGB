<script setup lang="ts">
/**
 * A sketch of the Devices page: everything on the desk in one window.
 *
 * The window chrome is hidden from assistive tech because it is decoration,
 * but the device list itself stays readable, since the breadth of what turns
 * up there is the point being made.
 */

const devices = [
  { name: 'Magma keyboard', lights: 6, effect: 'Ripple' },
  { name: 'Motherboard', lights: 12, effect: 'Ripple' },
  { name: 'Memory', lights: 20, effect: 'Ripple' },
  { name: 'Graphics card', lights: 8, effect: 'Ripple' },
  { name: 'Case fans', lights: 24, effect: 'Colour Wave' },
]

/** A fixed eight cell matrix per row, lit up to the device's light count, so
 *  every row lines up instead of each drawing a different width. */
const CELLS = 8

function matrix(count: number) {
  return Array.from({ length: CELLS }, (_, i) => i < Math.min(count, CELLS))
}

/** Full strings rather than a count glued to a noun, so "1 light" reads right. */
function lightsLabel(count: number) {
  return count === 1 ? '1 light' : `${count} lights`
}
</script>

<template>
  <div class="panel-figure">
    <div class="panel">
      <div class="panel__bar" aria-hidden="true">
        <BrandMark class="panel__icon" />
        <span class="panel__title">SickRGB</span>
      </div>

      <div class="panel__body">
        <!-- A div, not a nav: this is a picture of navigation, not navigation. -->
        <div class="panel__side" aria-hidden="true">
          <span>Effects</span>
          <span class="is-active">Devices</span>
          <span>Layout</span>
          <span>Settings</span>
        </div>

        <div class="panel__main">
          <div class="panel__head">
            <span class="panel__heading">Devices</span>
            <span class="panel__count tnum">{{ devices.length }} devices connected</span>
          </div>

          <ul class="panel__list">
            <li v-for="device in devices" :key="device.name" class="panel__row">
              <span class="panel__matrix" aria-hidden="true">
                <i v-for="(lit, index) in matrix(device.lights)" :key="index" :class="{ 'is-lit': lit }" />
              </span>

              <span class="panel__name">
                <span class="panel__label">{{ device.name }}</span>
                <small class="tnum">{{ lightsLabel(device.lights) }}</small>
              </span>

              <span class="panel__effect">{{ device.effect }}</span>
            </li>
          </ul>
        </div>
      </div>
    </div>
  </div>
</template>
