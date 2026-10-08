<script setup>
import { ref } from 'vue'

import TetrapodCalculator from './TetrapodCalculator.vue'

const mode = ref('tetrapod')
</script>

<template>
  <section class="calculators-tab">
    <div class="mode-switch" role="tablist" aria-label="Wybierz kalkulator">
      <button
        id="tetrapod-tab"
        type="button"
        role="tab"
        :aria-selected="mode === 'tetrapod'"
        aria-controls="tetrapod-panel"
        :class="{ active: mode === 'tetrapod' }"
        @click="mode = 'tetrapod'"
      >
        Tetrapod
        <small>Zbrojenie produkcyjne</small>
      </button>
      <button
        id="lego-tab"
        type="button"
        role="tab"
        :aria-selected="mode === 'lego'"
        aria-controls="lego-panel"
        :class="{ active: mode === 'lego' }"
        @click="mode = 'lego'"
      >
        LEGO
        <small>Bloczki i prefabrykaty</small>
      </button>
    </div>

    <div
      v-if="mode === 'tetrapod'"
      id="tetrapod-panel"
      role="tabpanel"
      aria-labelledby="tetrapod-tab"
    >
      <TetrapodCalculator />
    </div>
    <div
      v-else
      id="lego-panel"
      class="lego-panel"
      role="tabpanel"
      aria-labelledby="lego-tab"
    >
      <iframe title="Kalkulator bloczków LEGO" src="/kalkulator-lego"></iframe>
    </div>
  </section>
</template>

<style scoped>
.calculators-tab { display: grid; gap: 14px; }
.mode-switch {
  display: inline-grid;
  grid-template-columns: repeat(2, minmax(180px, 240px));
  justify-self: start;
  padding: 4px;
  border: 1px solid var(--border);
  border-radius: 9px;
  background: var(--sl-100);
}
.mode-switch button {
  display: grid;
  gap: 1px;
  padding: 9px 14px;
  border: 1px solid transparent;
  border-radius: 6px;
  background: transparent;
  color: var(--muted);
  font: 650 .88rem var(--font-body);
  text-align: left;
  cursor: pointer;
}
.mode-switch button small { font-size: .69rem; font-weight: 400; }
.mode-switch button.active {
  border-color: var(--border);
  background: var(--card);
  color: var(--text);
  box-shadow: var(--sh-xs);
}
.mode-switch button:focus-visible { outline: 2px solid var(--amber); outline-offset: 2px; }
.lego-panel {
  height: calc(100vh - 142px);
  min-height: 620px;
  margin: 0 -20px -40px;
  overflow: hidden;
  border-top: 1px solid var(--border);
  background: #0b0b0c;
}
.lego-panel iframe { display: block; width: 100%; height: 100%; border: 0; }
@media (max-width: 600px) {
  .mode-switch { width: 100%; grid-template-columns: 1fr 1fr; }
  .mode-switch button { min-width: 0; }
}
</style>
