export type RuntimeConfig = {
  apiBaseUrl: string;
  appDomain: string;
  sourceUrl: string;
  firebase: {
    apiKey: string;
    authDomain: string;
    projectId: string;
    storageBucket: string;
    messagingSenderId: string;
    appId: string;
    measurementId?: string;
  };
};

export const runtimeConfig: RuntimeConfig = {
  apiBaseUrl: import.meta.env.VITE_MINICLOUD_API_URL ?? "http://localhost:3210",
  appDomain: import.meta.env.VITE_MINICLOUD_APP_DOMAIN ?? "app.muni.dev",
  sourceUrl: import.meta.env.VITE_MINICLOUD_SOURCE_URL ?? "https://github.com/muniventures/minicloud",
  firebase: {
    apiKey: import.meta.env.VITE_FIREBASE_API_KEY ?? "",
    authDomain: import.meta.env.VITE_FIREBASE_AUTH_DOMAIN ?? "",
    projectId: import.meta.env.VITE_FIREBASE_PROJECT_ID ?? "",
    storageBucket: import.meta.env.VITE_FIREBASE_STORAGE_BUCKET ?? "",
    messagingSenderId: import.meta.env.VITE_FIREBASE_MESSAGING_SENDER_ID ?? "",
    appId: import.meta.env.VITE_FIREBASE_APP_ID ?? "",
    measurementId: import.meta.env.VITE_FIREBASE_MEASUREMENT_ID
  }
};
