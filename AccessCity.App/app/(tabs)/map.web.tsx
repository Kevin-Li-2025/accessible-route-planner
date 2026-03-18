import { StyleSheet, Text, View } from 'react-native';

export default function MapPageWeb() {
  return (
    <View style={styles.container}>
      <Text style={styles.text}>Map is available in the iOS and Android app.</Text>
      <Text style={styles.sub}>Open this project in Expo Go on a device to use navigation and voice guidance.</Text>
    </View>
  );
}

const styles = StyleSheet.create({
  container: {
    flex: 1,
    justifyContent: 'center',
    alignItems: 'center',
    padding: 24,
    backgroundColor: '#f8fafc',
  },
  text: {
    fontSize: 18,
    fontWeight: '600',
    color: '#0f172a',
    textAlign: 'center',
  },
  sub: {
    marginTop: 12,
    fontSize: 14,
    color: '#64748b',
    textAlign: 'center',
  },
});
