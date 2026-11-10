import {defineConfig} from '../../../../../modules/dashboard/node_modules/vite/dist/node/index.js';

import path from 'node:path';
const dashboard=path.resolve(import.meta.dirname,'../../../../../modules/dashboard');
export default defineConfig({root:import.meta.dirname,esbuild:{jsx:"automatic"},resolve:{alias:{'@':dashboard+'/app',react:dashboard+'/node_modules/react','react-dom':dashboard+'/node_modules/react-dom','lucide-react':dashboard+'/node_modules/lucide-react'}},server:{host:'127.0.0.1',port:3217,fs:{allow:[path.resolve(dashboard,'../..'),import.meta.dirname]}}});
