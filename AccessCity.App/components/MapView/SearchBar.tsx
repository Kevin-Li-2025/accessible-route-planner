import React from 'react';
import {
  StyleSheet,
  View,
  TextInput,
  TouchableOpacity,
} from 'react-native';
import { Ionicons } from '@expo/vector-icons';

type SearchBarProps = {
  value: string;
  onChangeText: (text: string) => void;
  onSubmitEditing: () => void;
  onClear: () => void;
};

export default function SearchBar({
  value,
  onChangeText,
  onSubmitEditing,
  onClear,
}: SearchBarProps) {
  return (
    <View style={styles.container}>
      <Ionicons name="search-outline" size={22} color="#9CA3AF" />

      <TextInput
        style={styles.input}
        placeholder="Search destination..."
        placeholderTextColor="#9CA3AF"
        value={value}
        onChangeText={onChangeText}
        onSubmitEditing={onSubmitEditing}
        returnKeyType="search"
      />

      {value.trim().length > 0 && (
        <TouchableOpacity onPress={onClear} style={styles.clearButton}>
          <Ionicons name="close-circle" size={22} color="#9CA3AF" />
        </TouchableOpacity>
      )}
    </View>
  );
}

const styles = StyleSheet.create({
  container: {
    position: 'absolute',
    top: 58,
    left: 16,
    right: 84,
    height: 56,
    backgroundColor: '#FFFFFF',
    borderRadius: 28,
    flexDirection: 'row',
    alignItems: 'center',
    paddingHorizontal: 16,
    elevation: 4,
    zIndex: 20,
  },

  input: {
    flex: 1,
    marginLeft: 10,
    fontSize: 18,
    color: '#111827',
  },

  clearButton: {
    marginLeft: 8,
    padding: 2,
  },
});