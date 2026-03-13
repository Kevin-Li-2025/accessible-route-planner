import { Platform } from 'react-native';

const MapView = Platform.select({
  web: () => require('./index.web').default,
  default: () => require('./index.native').default,
})();

export default MapView;
