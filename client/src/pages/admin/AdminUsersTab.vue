<script setup lang="ts">
import { ref, onMounted } from 'vue'
import type { AdminApi } from '../../composables/useAdminApi'
import { ROLES, ROLE_OPTIONS, ROLE_COLORS_LIGHT } from './roleConfig'
import { useToastStore } from '../../stores/toast'
import { errorMessage } from '../../utils/errors'
import { getInitials } from '../../utils/initials'

const toast = useToastStore()

interface User {
  id: number
  email: string
  display_name: string
  role: string
  is_active: number
}

interface UserForm {
  email: string
  displayName: string
  role: string
}

const props = defineProps<{
  api: AdminApi
}>()

const users = ref<User[]>([])
const showForm = ref(false)
const editingId = ref<number | null>(null)
const form = ref<UserForm>({ email: '', displayName: '', role: 'viewer' })
const error = ref('')
const saving = ref(false)

const loadUsers = () => {
  props.api
    .get<User[]>('/users')
    .then((data) => {
      users.value = data
    })
    .catch(() => {
      toast.error('Could not load users. Please try again.')
    })
}

onMounted(() => {
  loadUsers()
})

const resetForm = () => {
  form.value = { email: '', displayName: '', role: 'viewer' }
  showForm.value = false
  editingId.value = null
  error.value = ''
}

const handleSubmit = async () => {
  error.value = ''
  saving.value = true
  try {
    if (editingId.value) {
      await props.api.put(`/users/${editingId.value}`, {
        displayName: form.value.displayName,
        role: form.value.role,
      })
    } else {
      await props.api.post('/users', {
        email: form.value.email,
        displayName: form.value.displayName,
        role: form.value.role,
      })
    }
    resetForm()
    loadUsers()
  } catch (err) {
    error.value = errorMessage(err, 'Could not save user. Please try again.')
  } finally {
    saving.value = false
  }
}

const startEdit = (user: User) => {
  form.value = { email: user.email, displayName: user.display_name, role: user.role }
  editingId.value = user.id
  showForm.value = true
}

const toggleActive = async (user: User) => {
  try {
    await props.api.put(`/users/${user.id}`, { is_active: user.is_active ? 0 : 1 })
    loadUsers()
  } catch (err) {
    error.value = errorMessage(err, 'Could not update user. Please try again.')
  }
}
</script>

<template>
  <div class="space-y-6">
    <div class="flex items-center justify-between">
      <div>
        <h2 class="font-display text-lg font-bold text-csub-blue-dark uppercase tracking-wide">
          Admin Users
        </h2>
        <p class="font-body text-xs text-csub-gray mt-1">
          Manage who can access the admin portal and their permission level
        </p>
      </div>
      <button
        v-if="!showForm"
        @click="
          () => {
            resetForm()
            showForm = true
          }
        "
        class="flex items-center gap-2 bg-csub-blue hover:bg-csub-blue-dark text-white font-display text-sm font-bold uppercase tracking-wider px-4 py-2 rounded-lg shadow transition-colors"
      >
        <svg
          class="w-4 h-4"
          fill="none"
          viewBox="0 0 24 24"
          stroke="currentColor"
          :stroke-width="2"
        >
          <path stroke-linecap="round" stroke-linejoin="round" d="M12 4v16m8-8H4" />
        </svg>
        New User
      </button>
    </div>

    <form
      v-if="showForm"
      @submit.prevent="handleSubmit"
      class="bg-white rounded-xl border border-gray-200 shadow-sm p-5 space-y-4"
    >
      <h3 class="font-display text-sm font-bold uppercase tracking-wide text-csub-blue-dark">
        {{ editingId ? 'Edit User' : 'Create User' }}
      </h3>
      <div class="grid grid-cols-1 sm:grid-cols-2 gap-4">
        <input
          type="text"
          required
          placeholder="Display Name"
          v-model="form.displayName"
          class="px-3 py-2 rounded-lg border border-gray-300 font-body text-sm focus:outline-none focus:ring-2 focus:ring-csub-blue"
        />
        <input
          type="email"
          :required="!editingId"
          :disabled="!!editingId"
          placeholder="Email"
          v-model="form.email"
          class="px-3 py-2 rounded-lg border border-gray-300 font-body text-sm focus:outline-none focus:ring-2 focus:ring-csub-blue disabled:bg-gray-100"
        />
        <select
          v-model="form.role"
          class="px-3 py-2 rounded-lg border border-gray-300 font-body text-sm focus:outline-none focus:ring-2 focus:ring-csub-blue"
        >
          <option v-for="r in ROLE_OPTIONS" :key="r" :value="r">{{ ROLES[r]?.label || r }}</option>
        </select>
      </div>
      <p v-if="error" class="text-red-600 text-sm font-body">{{ error }}</p>
      <div class="flex gap-3">
        <button
          type="submit"
          :disabled="saving"
          class="bg-csub-blue hover:bg-csub-blue-dark text-white font-display text-sm font-bold uppercase tracking-wider px-5 py-2 rounded-lg shadow transition-colors disabled:opacity-50"
        >
          {{ saving ? 'Saving...' : editingId ? 'Update' : 'Create' }}
        </button>
        <button
          type="button"
          @click="resetForm"
          class="font-body text-sm text-csub-gray hover:text-csub-blue-dark transition-colors"
        >
          Cancel
        </button>
      </div>
    </form>

    <div class="space-y-2">
      <div
        v-for="user in users"
        :key="user.id"
        :class="`flex items-center justify-between bg-white rounded-xl border border-gray-200 shadow-sm px-5 py-4 ${
          !user.is_active ? 'opacity-50' : ''
        }`"
      >
        <div class="flex items-center gap-4">
          <div
            class="w-10 h-10 rounded-full bg-csub-blue/10 flex items-center justify-center font-display text-sm font-bold text-csub-blue-dark"
          >
            {{ getInitials(user.display_name) }}
          </div>
          <div>
            <p class="font-body text-sm font-semibold text-gray-900">{{ user.display_name }}</p>
            <p class="font-body text-xs text-csub-gray">{{ user.email }}</p>
          </div>
          <span
            :class="`text-xs px-2 py-0.5 rounded-full font-body font-medium ${ROLE_COLORS_LIGHT[user.role] || 'bg-gray-100 text-gray-600'}`"
          >
            {{ ROLES[user.role]?.label || user.role }}
          </span>
        </div>
        <div class="flex items-center gap-2">
          <button
            @click="startEdit(user)"
            class="font-body text-xs text-csub-blue hover:text-csub-blue-dark transition-colors"
          >
            Edit
          </button>
          <button
            @click="toggleActive(user)"
            :class="`font-body text-xs transition-colors ${
              user.is_active
                ? 'text-red-500 hover:text-red-700'
                : 'text-green-600 hover:text-green-800'
            }`"
          >
            {{ user.is_active ? 'Deactivate' : 'Activate' }}
          </button>
        </div>
      </div>
    </div>
  </div>
</template>
