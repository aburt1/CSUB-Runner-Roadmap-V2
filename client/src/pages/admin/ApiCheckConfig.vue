<script setup lang="ts">
import { ref, watch } from 'vue'
import type { AdminApi } from '../../composables/useAdminApi'
import { errorMessage } from '../../utils/errors'

// Sentinel the server returns/accepts for "unchanged stored credentials".
// Hoisted to a const so a typo can't silently corrupt stored credentials.
const MASKED_CREDENTIALS = '••••••••'

interface HeaderEntry {
  key: string
  value: string
}

interface ApiCheckConfigData {
  configured: boolean
  is_enabled: boolean
  http_method: string
  url: string
  auth_type: string
  auth_credentials: string
  student_param_source: string
  response_field_path: string
  headers: HeaderEntry[]
}

interface TestResultData {
  error?: string
  statusCode?: number
  extractedValue?: unknown
  wouldMarkComplete?: boolean
  responseBody?: string
}

const props = defineProps<{
  stepId: number
  api: AdminApi
}>()

const config = ref<ApiCheckConfigData | null>(null)
const loading = ref(true)
const saving = ref(false)
const testLoading = ref(false)
const testResult = ref<TestResultData | null>(null)
const expanded = ref(false)
const error = ref<string | null>(null)
const success = ref<string | null>(null)

// Form state
const enabled = ref(false)
const httpMethod = ref('GET')
const url = ref('')
const authType = ref('none')
const username = ref('')
const password = ref('')
const bearerToken = ref('')
const headers = ref<HeaderEntry[]>([])
const studentParamSource = ref('emplid')
const responseFieldPath = ref('')
const testStudentId = ref('')

const field =
  'w-full px-3 py-2 rounded-lg border border-gray-300 bg-white font-body text-sm focus:outline-none focus:ring-1 focus:ring-csub-blue'
const label = 'block font-body text-xs font-semibold text-csub-blue-dark mb-1'

const fetchConfig = async () => {
  try {
    const data = await props.api.get<ApiCheckConfigData>(`/steps/${props.stepId}/api-check`)
    config.value = data
    if (data.configured) {
      enabled.value = data.is_enabled
      httpMethod.value = data.http_method || 'GET'
      url.value = data.url || ''
      authType.value = data.auth_type || 'none'
      studentParamSource.value = data.student_param_source || 'emplid'
      responseFieldPath.value = data.response_field_path || ''
      if (data.auth_credentials === MASKED_CREDENTIALS) {
        if (data.auth_type === 'basic') {
          username.value = MASKED_CREDENTIALS
          password.value = MASKED_CREDENTIALS
        } else if (data.auth_type === 'bearer') {
          bearerToken.value = MASKED_CREDENTIALS
        }
      }
      if (Array.isArray(data.headers)) {
        headers.value = data.headers
      }
      expanded.value = true
    }
  } catch {
    // No config yet — that's fine
  } finally {
    loading.value = false
  }
}

watch(
  () => props.stepId,
  (id) => {
    if (id) fetchConfig()
  },
  { immediate: true },
)

const handleSave = async () => {
  error.value = null
  success.value = null
  saving.value = true
  try {
    let authCredentials: string | null = null
    if (authType.value === 'basic') {
      const usernameMasked = username.value === MASKED_CREDENTIALS
      const passwordMasked = password.value === MASKED_CREDENTIALS
      if (usernameMasked && passwordMasked) {
        // Untouched — the server keeps the stored credentials.
        authCredentials = MASKED_CREDENTIALS
      } else if (usernameMasked || passwordMasked) {
        // A half-edited pair would store the literal mask as the real value,
        // silently corrupting the saved credentials.
        error.value = 'To change Basic credentials, re-enter both the username and the password.'
        return
      } else {
        authCredentials = JSON.stringify({ username: username.value, password: password.value })
      }
    } else if (authType.value === 'bearer') {
      if (bearerToken.value !== MASKED_CREDENTIALS) {
        authCredentials = JSON.stringify({ token: bearerToken.value })
      } else {
        authCredentials = MASKED_CREDENTIALS
      }
    }

    await props.api.put(`/steps/${props.stepId}/api-check`, {
      is_enabled: enabled.value,
      http_method: httpMethod.value,
      url: url.value,
      auth_type: authType.value,
      auth_credentials: authCredentials,
      headers: headers.value.filter((h) => h.key),
      student_param_source: studentParamSource.value,
      response_field_path: responseFieldPath.value,
    })
    // Inline confirmation — shown next to the form being edited rather than as a
    // floating toast, so the admin can immediately see which config was saved.
    success.value = 'API check saved'
    setTimeout(() => {
      success.value = null
    }, 3000)
  } catch (err) {
    error.value = errorMessage(err, 'Could not save the API check. Please try again.')
  } finally {
    saving.value = false
  }
}

const handleDelete = async () => {
  if (!confirm('Remove API check configuration for this step?')) return
  try {
    await props.api.del(`/steps/${props.stepId}/api-check`)
    config.value = null
    url.value = ''
    responseFieldPath.value = ''
    authType.value = 'none'
    headers.value = []
    expanded.value = false
    // Inline confirmation — shown next to the form being edited.
    success.value = 'API check removed'
    setTimeout(() => {
      success.value = null
    }, 3000)
  } catch (err) {
    error.value = errorMessage(err, 'Could not remove the API check. Please try again.')
  }
}

const handleTest = async () => {
  if (!testStudentId.value) return
  testLoading.value = true
  testResult.value = null
  try {
    const result = await props.api.post<TestResultData>(`/steps/${props.stepId}/api-check/test`, {
      testStudentId: testStudentId.value,
    })
    testResult.value = result
  } catch (err) {
    testResult.value = { error: errorMessage(err, 'Test failed') }
  } finally {
    testLoading.value = false
  }
}

const addHeader = () => {
  headers.value = [...headers.value, { key: '', value: '' }]
}
const removeHeader = (idx: number) => {
  headers.value = headers.value.filter((_, i) => i !== idx)
}
const updateHeader = (idx: number, prop: keyof HeaderEntry, value: string) => {
  const updated = [...headers.value]
  updated[idx] = { ...updated[idx]!, [prop]: value }
  headers.value = updated
}

const handleTestKeydown = (e: KeyboardEvent) => {
  if (e.key === 'Enter') {
    e.preventDefault()
    handleTest()
  }
}
</script>

<template>
  <div v-if="!loading" class="mt-4 border border-gray-200 rounded-lg">
    <button
      type="button"
      @click="expanded = !expanded"
      class="w-full flex items-center justify-between px-4 py-3 text-left hover:bg-gray-50 rounded-lg transition-colors"
    >
      <span class="font-display text-sm font-bold text-csub-blue-dark uppercase tracking-wider">
        API Check
        <span v-if="config?.configured" class="text-green-600 text-xs font-body normal-case ml-2"
          >Configured</span
        >
      </span>
      <span class="text-csub-gray text-xs">{{ expanded ? '▲' : '▼' }}</span>
    </button>

    <div v-if="expanded" class="px-4 pb-4 space-y-3">
      <p v-if="error" class="text-red-600 text-xs font-body">{{ error }}</p>
      <p v-if="success" class="text-green-600 text-xs font-body">{{ success }}</p>

      <div class="flex items-center gap-3">
        <label class="font-body text-sm text-csub-blue-dark font-semibold">Enable</label>
        <button
          type="button"
          @click="enabled = !enabled"
          :class="`relative w-10 h-5 rounded-full transition-colors ${enabled ? 'bg-csub-blue' : 'bg-gray-300'}`"
        >
          <span
            :class="`absolute top-0.5 left-0.5 w-4 h-4 bg-white rounded-full shadow transition-transform ${enabled ? 'translate-x-5' : ''}`"
          />
        </button>
      </div>

      <div class="grid grid-cols-2 gap-3">
        <div>
          <label :class="label">HTTP Method</label>
          <select v-model="httpMethod" :class="field">
            <option value="GET">GET</option>
            <option value="POST">POST</option>
          </select>
        </div>
        <div>
          <label :class="label">Student Parameter Source</label>
          <select v-model="studentParamSource" :class="field">
            <option value="emplid">Campus ID (emplid)</option>
            <option value="email">Email</option>
          </select>
        </div>
      </div>

      <div>
        <label :class="label">URL</label>
        <input
          type="text"
          v-model="url"
          :class="field"
          placeholder="https://api.example.com/check/{{studentId}}"
        />
        <p class="font-body text-xs text-csub-gray mt-0.5">
          Use <span v-pre>{{ studentId }}</span> as placeholder for the student identifier
        </p>
      </div>

      <div>
        <label :class="label">Authentication Type</label>
        <select v-model="authType" :class="field">
          <option value="none">None</option>
          <option value="basic">Basic</option>
          <option value="bearer">Bearer Token</option>
        </select>
      </div>

      <div v-if="authType === 'basic'" class="grid grid-cols-2 gap-3">
        <div>
          <label :class="label">Username</label>
          <input type="text" v-model="username" :class="field" />
        </div>
        <div>
          <label :class="label">Password</label>
          <input type="password" v-model="password" :class="field" />
        </div>
      </div>

      <div v-if="authType === 'bearer'">
        <label :class="label">Bearer Token</label>
        <input type="password" v-model="bearerToken" :class="field" />
      </div>

      <div>
        <label :class="label">Custom Headers</label>
        <div v-for="(h, i) in headers" :key="i" class="flex gap-2 mb-1">
          <input
            type="text"
            :value="h.key"
            @input="updateHeader(i, 'key', ($event.target as HTMLInputElement).value)"
            :class="field"
            placeholder="Header name"
          />
          <input
            type="text"
            :value="h.value"
            @input="updateHeader(i, 'value', ($event.target as HTMLInputElement).value)"
            :class="field"
            placeholder="Value"
          />
          <button
            type="button"
            @click="removeHeader(i)"
            class="text-red-500 text-xs font-body hover:text-red-700 shrink-0"
          >
            Remove
          </button>
        </div>
        <button
          type="button"
          @click="addHeader"
          class="text-csub-blue text-xs font-body hover:underline"
        >
          + Add Header
        </button>
      </div>

      <div>
        <label :class="label">Response Field Path</label>
        <input
          type="text"
          v-model="responseFieldPath"
          :class="field"
          placeholder="data.is_complete"
        />
        <p class="font-body text-xs text-csub-gray mt-0.5">
          Dot-notation path to the boolean field in the JSON response
        </p>
      </div>

      <div class="flex gap-2 pt-1">
        <button
          type="button"
          @click="handleSave"
          :disabled="saving || !url || !responseFieldPath"
          class="bg-csub-blue hover:bg-csub-blue-dark disabled:opacity-50 text-white font-display font-bold uppercase tracking-wider px-4 py-2 rounded-lg shadow transition-colors text-xs"
        >
          {{ saving ? 'Saving...' : 'Save API Check' }}
        </button>
        <button
          v-if="config?.configured"
          type="button"
          @click="handleDelete"
          class="border border-red-300 text-red-600 hover:bg-red-50 font-body px-4 py-2 rounded-lg transition-colors text-xs"
        >
          Remove
        </button>
      </div>

      <!-- Test Section -->
      <div class="border-t border-gray-200 pt-3 mt-3">
        <label :class="label">Test API Check</label>
        <div class="flex gap-2">
          <input
            type="text"
            v-model="testStudentId"
            @keydown="handleTestKeydown"
            :class="field"
            placeholder="Test student ID (e.g., 001001000)"
          />
          <button
            type="button"
            @click="handleTest"
            :disabled="testLoading || !testStudentId"
            class="shrink-0 bg-gray-100 hover:bg-gray-200 disabled:opacity-50 text-csub-blue-dark font-body font-semibold px-4 py-2 rounded-lg transition-colors text-xs"
          >
            {{ testLoading ? 'Testing...' : 'Test' }}
          </button>
        </div>
        <div v-if="testResult" class="mt-2 p-3 bg-gray-50 rounded-lg text-xs font-body space-y-1">
          <p v-if="testResult.error" class="text-red-600">{{ testResult.error }}</p>
          <template v-else>
            <p><span class="font-semibold">Status:</span> {{ testResult.statusCode }}</p>
            <p>
              <span class="font-semibold">Extracted Value:</span>
              {{ JSON.stringify(testResult.extractedValue) }}
            </p>
            <p>
              <span class="font-semibold">Would Mark Complete:</span>
              {{ testResult.wouldMarkComplete ? 'Yes' : 'No' }}
            </p>
            <details class="mt-1">
              <summary class="cursor-pointer text-csub-gray hover:text-csub-blue-dark">
                Raw Response
              </summary>
              <pre
                class="mt-1 p-2 bg-white rounded border text-xs overflow-x-auto max-h-40 overflow-y-auto"
                >{{ testResult.responseBody }}</pre
              >
            </details>
          </template>
        </div>
      </div>
    </div>
  </div>
</template>
