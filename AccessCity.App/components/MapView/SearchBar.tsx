import React from 'react';
import {
  StyleSheet,
  View,
  TextInput,
  TouchableOpacity,
  Text,
  ScrollView,
} from 'react-native';
import { Ionicons } from '@expo/vector-icons';

export type SearchSuggestion = {
  id: string;
  title: string;
  subtitle?: string;
};

type SearchBarProps = {
  value: string;
  onChangeText: (text: string) => void;
  onSubmitEditing: () => void;
  onClear: () => void;
  suggestions?: SearchSuggestion[];
  onSuggestionPress?: (suggestion: SearchSuggestion) => void;
};

export default function SearchBar({
  value,
  onChangeText,
  onSubmitEditing,
  onClear,
  suggestions = [],
  onSuggestionPress,
}: SearchBarProps) {
  const showSuggestions = suggestions.length > 0 && typeof onSuggestionPress === 'function';

  return (
    <View style={styles.wrapper}>
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

      {showSuggestions && (
        <ScrollView
          style={styles.suggestionsContainer}
          contentContainerStyle={styles.suggestionsContent}
          keyboardShouldPersistTaps="handled"
        >
          {suggestions.map((suggestion, index) => (
            <TouchableOpacity
              key={suggestion.id}
              style={[
                styles.suggestionItem,
                index === suggestions.length - 1 && styles.suggestionItemLast,
              ]}
              onPress={() => onSuggestionPress(suggestion)}
            >
              <Ionicons name="location-outline" size={18} color="#0F3D91" />

              <View style={styles.suggestionTextContainer}>
                <Text style={styles.suggestionTitle} numberOfLines={1}>
                  {suggestion.title}
                </Text>

                {!!suggestion.subtitle && (
                  <Text style={styles.suggestionSubtitle} numberOfLines={2}>
                    {suggestion.subtitle}
                  </Text>
                )}
              </View>
            </TouchableOpacity>
          ))}
        </ScrollView>
      )}
    </View>
  );
}

const styles = StyleSheet.create({
  wrapper: {
    position: 'absolute',
    top: 58,
    left: 16,
    right: 84,
    zIndex: 20,
  },

  container: {
    height: 56,
    backgroundColor: '#FFFFFF',
    borderRadius: 28,
    flexDirection: 'row',
    alignItems: 'center',
    paddingHorizontal: 16,
    elevation: 4,
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

  suggestionsContainer: {
    marginTop: 8,
    maxHeight: 260,
    backgroundColor: '#FFFFFF',
    borderRadius: 20,
    elevation: 5,
  },

  suggestionsContent: {
    paddingVertical: 6,
  },

  suggestionItem: {
    flexDirection: 'row',
    alignItems: 'flex-start',
    paddingHorizontal: 16,
    paddingVertical: 12,
    borderBottomWidth: 1,
    borderBottomColor: '#E5E7EB',
  },

  suggestionItemLast: {
    borderBottomWidth: 0,
  },

  suggestionTextContainer: {
    flex: 1,
    marginLeft: 10,
  },

  suggestionTitle: {
    fontSize: 15,
    fontWeight: '600',
    color: '#111827',
  },

  suggestionSubtitle: {
    marginTop: 2,
    fontSize: 13,
    color: '#6B7280',
    lineHeight: 18,
  },
});
