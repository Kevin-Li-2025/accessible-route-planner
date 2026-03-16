import React from 'react';
import { StyleSheet } from 'react-native';
import Animated, { FadeInUp, FadeOut } from 'react-native-reanimated';
import { Ionicons } from '@expo/vector-icons';

interface Props {
  message?: string;
  visible?: boolean;
}

export function ErrorMessage({ message, visible }: Props) {
  if (!visible || !message) return null;

  return (
    <Animated.View 
      entering={FadeInUp.duration(300)} 
      exiting={FadeOut.duration(200)}
      style={styles.container}
    >
      <Ionicons name="alert-circle" size={16} color="#EF4444" style={styles.icon} />
      <Animated.Text style={styles.text}>{message}</Animated.Text>
    </Animated.View>
  );
}

const styles = StyleSheet.create({
  container: {
    flexDirection: 'row',
    alignItems: 'center',
    marginTop: 6,
    paddingHorizontal: 4,
  },
  icon: {
    marginRight: 6,
  },
  text: {
    color: '#EF4444',
    fontSize: 13,
    fontWeight: '600',
  },
});
