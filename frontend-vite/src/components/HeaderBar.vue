<script setup>
import { LogOut } from 'lucide-vue-next'
defineProps({ user: Object })
const emit = defineEmits(['logout'])
</script>

<template>
  <header>
    <span class="header-wordmark">FactoryFlow ERP Demo</span>
    <span class="header-divider" aria-hidden="true"></span>
    <span class="role-badge">{{ user?.name }}</span>
    <button class="btn-logout" @click="emit('logout')" title="Wyloguj" aria-label="Wyloguj">
      <LogOut :size="15" />
      <span>Wyloguj</span>
    </button>
  </header>
</template>

<style scoped>
.header-wordmark {
  font-weight: 700;
  font-size: 0.95rem;
  color: #fff;
  letter-spacing: 0.01em;
}
.header-divider {
  width: 1px;
  height: 16px;
  background: rgba(255,255,255,0.15);
  margin: 0 4px;
}
.btn-logout {
  margin-left: 8px;
  display: inline-flex;
  align-items: center;
  gap: 5px;
  padding: 5px 11px;
  background: rgba(255,255,255,0.08);
  border: 1px solid rgba(255,255,255,0.14);
  border-radius: 5px;
  color: rgba(255,255,255,0.8);
  font-size: 0.8rem;
  font-family: inherit;
  cursor: pointer;
  transition: background 140ms, border-color 140ms;
}
.btn-logout:hover {
  background: rgba(255,255,255,0.14);
  border-color: rgba(255,255,255,0.24);
  color: #fff;
}
.btn-logout:focus-visible {
  outline: 2px solid var(--amber);
  outline-offset: 2px;
}
</style>
