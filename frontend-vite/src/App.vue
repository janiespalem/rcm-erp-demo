<script setup>
import LoginScreen from './components/LoginScreen.vue'
import AppShell    from './components/AppShell.vue'
import { useAuth } from './composables/useAuth'

const { currentUser } = useAuth()
</script>

<template>
  <LoginScreen v-if="!currentUser" />
  <AppShell    v-else />
</template>
