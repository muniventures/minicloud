import { initializeApp } from "firebase/app";
import { createUserWithEmailAndPassword, getAuth, GoogleAuthProvider, signInWithEmailAndPassword, signInWithPopup, signOut } from "firebase/auth";
import { runtimeConfig } from "../config/runtimeConfig";

const firebaseApp = initializeApp(runtimeConfig.firebase);

export const auth = getAuth(firebaseApp);

export async function getCurrentIdToken(): Promise<string | null> {
  if (!auth.currentUser) {
    return null;
  }

  return auth.currentUser.getIdToken();
}

export function signOutOfFirebase() {
  return signOut(auth);
}

export function signInWithEmail(email: string, password: string) {
  return signInWithEmailAndPassword(auth, email, password);
}

export function signUpWithEmail(email: string, password: string) {
  return createUserWithEmailAndPassword(auth, email, password);
}

export function signInWithGoogle() {
  return signInWithPopup(auth, new GoogleAuthProvider());
}
