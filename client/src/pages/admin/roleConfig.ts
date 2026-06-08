interface RoleInfo {
  label: string;
  color: string;
}

export const ROLES: Record<string, RoleInfo> = {
  sysadmin:          { label: 'System Admin',      color: 'bg-white/20 text-white' },
  admissions_editor: { label: 'Admissions Editor',  color: 'bg-white/20 text-white' },
  admissions:        { label: 'Admissions',          color: 'bg-white/20 text-white' },
  viewer:            { label: 'Viewer',              color: 'bg-white/20 text-white' },
};

export const ROLE_COLORS_LIGHT: Record<string, string> = {
  sysadmin:          'bg-csub-blue-dark/10 text-csub-blue-dark',
  admissions_editor: 'bg-blue-50 text-blue-700',
  admissions:        'bg-amber-50 text-amber-700',
  viewer:            'bg-gray-100 text-gray-600',
};

export const ROLE_OPTIONS: string[] = ['viewer', 'admissions', 'admissions_editor', 'sysadmin'];
