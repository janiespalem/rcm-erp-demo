<script setup>
import { useToast } from '@/composables/useToast'
const { toasts } = useToast()
</script>

<template>
  <div class="toast-container">
    <div v-for="t in toasts" :key="t.id" :class="['toast', 'toast-'+t.type]">
      {{ t.message }}
    </div>
  </div>
</template>

<style scoped>
.toast-container {
  position: fixed;
  bottom: 24px;
  right: 24px;
  z-index: 9999;
  display: flex;
  flex-direction: column;
  gap: 8px;
  pointer-events: none;
}
.toast {
  padding: 12px 18px;
  border-radius: 8px;
  font-size: 0.88rem;
  font-weight: 600;
  max-width: 360px;
  box-shadow: 0 4px 16px rgba(0,0,0,0.18);
  animation: slide-in 0.2s ease;
}
.toast-error   { background: #c62828; color: #fff; }
.toast-success { background: #2e7d32; color: #fff; }
.toast-warn    { background: #f57f17; color: #fff; }
@keyframes slide-in {
  from { transform: translateX(40px); opacity: 0; }
  to   { transform: translateX(0);    opacity: 1; }
}
</style>
