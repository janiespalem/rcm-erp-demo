<script setup>
import { useConfirm } from '@/composables/useConfirm'
const { pending, respond } = useConfirm()
</script>

<template>
  <div v-if="pending"
       style="position:fixed;inset:0;background:rgba(0,0,0,.45);display:flex;align-items:center;justify-content:center;z-index:200"
       role="dialog"
       aria-modal="true"
       aria-labelledby="confirm-message"
       @keydown.esc="respond(false)"
       @click.self="respond(false)">
    <div style="background:var(--card);border:1px solid var(--border);border-radius:var(--radius);padding:24px 28px;max-width:360px;width:90%;box-shadow:0 8px 32px rgba(0,0,0,.35)">
      <p id="confirm-message" style="font-size:0.95rem;font-weight:600;margin-bottom:20px;line-height:1.45">{{ pending.message }}</p>
      <div style="display:flex;gap:8px;justify-content:flex-end">
        <button class="btn btn-outline btn-sm" autofocus @click="respond(false)">Anuluj</button>
        <button class="btn btn-sm" style="background:var(--rcm-red);color:#fff" @click="respond(true)">{{ pending.confirmLabel }}</button>
      </div>
    </div>
  </div>
</template>
