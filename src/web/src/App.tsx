import { QueryClientProvider, useQueryClient } from '@tanstack/react-query'
import { useEffect, useState } from 'react'
import { BrowserRouter, Route, Routes } from 'react-router-dom'
import { setUnauthorizedHandler } from './api/http'
import { createQueryClient, endSession } from './queryClient'
import RequireAuth from './components/RequireAuth'
import DepartmentPage from './pages/DepartmentPage'
import DepartmentsPage from './pages/DepartmentsPage'
import StatusPage from './pages/StatusPage'
import TeamPage from './pages/TeamPage'

export function AppRoutes() {
  const qc = useQueryClient()
  useEffect(() => {
    // Any 401 means the session ended: drop cached data and show the sign-in screen.
    setUnauthorizedHandler(() => endSession(qc))
    return () => setUnauthorizedHandler(null)
  }, [qc])

  return (
    <Routes>
      <Route element={<RequireAuth />}>
        <Route index element={<DepartmentsPage />} />
        <Route path="departments/:departmentId" element={<DepartmentPage />} />
        <Route path="departments/:departmentId/teams/:teamId" element={<TeamPage />} />
        <Route path="status" element={<StatusPage />} />
        <Route path="*" element={<p>Page not found.</p>} />
      </Route>
    </Routes>
  )
}

export default function App() {
  const [client] = useState(createQueryClient)
  return (
    <QueryClientProvider client={client}>
      <BrowserRouter>
        <AppRoutes />
      </BrowserRouter>
    </QueryClientProvider>
  )
}
