import { useCallback, useEffect, useState } from "react";
import { SchemaRenderer } from "./sdui/SchemaRenderer";
import { DuiContext, type ThemeMode } from "./sdui/dui-context";
import { useSchema } from "./sdui/useSchema";
import "./App.css";

const THEME_KEY = "dui-theme";

// First load: honor a saved choice, else fall back to the OS preference.
function initialTheme(): ThemeMode {
  const saved = localStorage.getItem(THEME_KEY);
  if (saved === "dark" || saved === "light") return saved;
  return window.matchMedia?.("(prefers-color-scheme: dark)").matches ? "dark" : "light";
}

export default function App() {
  const { schema, version, connected } = useSchema();
  const [theme, setTheme] = useState<ThemeMode>(initialTheme);

  // One `dark` class on <html> drives every dark: variant in the rendered tree; persist the choice.
  useEffect(() => {
    document.documentElement.classList.toggle("dark", theme === "dark");
    localStorage.setItem(THEME_KEY, theme);
  }, [theme]);

  // The client-side action dispatcher buttons call via props.onClick={action}.
  const dispatch = useCallback((action: string) => {
    if (action === "toggleTheme") setTheme((t) => (t === "dark" ? "light" : "dark"));
  }, []);

  // The schema IS the page (it renders its own full-bleed header/main/footer). The only
  // operator chrome is a small fixed pill showing the live connection + current version.
  return (
    <DuiContext.Provider value={{ theme, dispatch }}>
      <SchemaRenderer schema={schema} />
      <div className="fixed bottom-4 right-4 z-50 flex items-center gap-1.5 rounded-full border border-gray-200 bg-white/90 px-3 py-1.5 text-xs font-medium text-gray-500 shadow-sm backdrop-blur dark:border-gray-800 dark:bg-gray-900/90 dark:text-gray-400">
        <span className={`h-1.5 w-1.5 rounded-full ${connected ? "bg-emerald-500" : "bg-gray-300"}`} />
        {connected ? "live" : "disconnected"} · v{version}
      </div>
    </DuiContext.Provider>
  );
}
